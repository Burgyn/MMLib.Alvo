/* The F5 admin dashboard, drawn.
   ==============================

   A design artifact: plain ES modules, no build step, no framework. Its job is to settle the
   dashboard's shape before a Razor component is written.

   Three rules this file follows, each of which it broke once:

   1. **Nothing about Alvo is written from memory.** Refusals, warnings, management routes, field
      facets and the descriptor itself come from `generated/`, which `scripts/gen-prototype-fixtures`
      writes from the repository.
   2. **There is one working copy.** Every editor mutates `working-copy.js`'s document; the count in
      the shell, the pending bar, the descriptor pane and the preview all read that one diff.
   3. **No client evaluates a stored row.** The simulator renders `ManagementPolicyVerdict`'s shape
      through `policy.js` and stops there — a per-record verdict is a second policy evaluator.

   Design: docs/superpowers/specs/2026-09-18-f5-admin-dashboard-design.md */

import { CAPABILITIES } from './generated/capabilities.js';
import { SCHEMA_FACETS } from './generated/schema-facets.js';
import {
  wc, editors, entities, entityView, declaredRoles, accessBlock, declaredFormats,
  tenancyEnabled, declaresBlock, changes, count, grouped, touchesAccess, workingPlan,
  renderLines, plan as planBetween, apply as applyWorking, restore as restoreRevision,
  discard as discardWorking, reset as resetWorking, startEmpty, KINDS,
} from './working-copy.js';
import { verdict, OPERATIONS, CAUSES, outcomeOfFailingUsing, ALLOWED_MEANS } from './policy.js';
import { TENANTS, USERS, BOOTSTRAP, BUILTIN_ROLES, ROWS, ROW_COUNTS, INFO, PALETTE_ITEMS, GOTO } from './sample-rows.js';
import { DECISIONS, REJECTED, OPEN_QUESTIONS, COMPONENTS } from './notes.js';

/* ==========================================================================
   State
   ========================================================================== */

const state = {
  route: location.hash || '#/overview',
  overlay: null,
  entity: 'work_orders',
  tab: 'fields',
  selectedField: null,
  selectedRows: new Set(),
  screenState: 'ready',
  ruleOpen: null,
  simulate: { user: USERS[2].id, operation: 'list' },
  compareA: 6,
  compareB: 7,
  person: null,
  pickerOpen: null,
  pickerChosen: {},
  form: { errors: [], values: {} },
  filterFields: '',
  columns: null,
  applyState: null,      // null | 'stale' | 'refused-destructive'
  signedIn: BOOTSTRAP.id,
  membership: Object.fromEntries(USERS.map((u) => [u.id, { roleNames: [...u.roleNames], tenant: u.tenant, isDisabled: u.isDisabled }])),
  membershipLog: [],
  credentialToken: null,
  paletteIndex: 0,
  paletteQuery: '',
  lastFocus: null,
};

/* ==========================================================================
   Small helpers
   ========================================================================== */

const $ = (sel, root = document) => root.querySelector(sel);
const $$ = (sel, root = document) => [...root.querySelectorAll(sel)];
const esc = (s) => String(s ?? '').replace(/[&<>"]/g, (c) => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;' }[c]));
const num = (n) => Number(n).toLocaleString('en-US');
const eur = (n) => Number(n).toLocaleString('en-US', { style: 'currency', currency: 'EUR' });
const titleCase = (s) => String(s).replace(/_/g, ' ').replace(/^./, (c) => c.toUpperCase());

function icon(name) {
  const p = {
    grid: '<path d="M3 3h6v6H3zM11 3h6v6h-6zM3 11h6v6H3zM11 11h6v6h-6z"/>',
    schema: '<path d="M4 4h5v4H4zM11 8h5v4h-5zM4 12h5v4H4z"/><path d="M9 6h2v4M9 14h2v-2"/>',
    rows: '<path d="M3 5h14M3 10h14M3 15h14"/>',
    shield: '<path d="M10 3l6 2v5c0 4-3 6-6 7-3-1-6-3-6-7V5z"/>',
    rule: '<path d="M4 6h12M4 10h8M4 14h5"/><circle cx="15" cy="13" r="2.4"/>',
    clock: '<circle cx="10" cy="10" r="7"/><path d="M10 6v4l3 2"/>',
    bolt: '<path d="M11 2L4 11h5l-1 7 7-9h-5z"/>',
    fn: '<path d="M7 16c2 0 2-4 2-6s0-6 2-6"/><path d="M6 10h7"/>',
    plug: '<path d="M7 3v5M13 3v5"/><path d="M4 8h12v2a6 6 0 01-12 0z"/><path d="M10 16v2"/>',
    cog: '<circle cx="10" cy="10" r="3"/><path d="M10 2v2M10 16v2M2 10h2M16 10h2M4.5 4.5l1.5 1.5M14 14l1.5 1.5M15.5 4.5L14 6M6 14l-1.5 1.5"/>',
    search: '<circle cx="9" cy="9" r="5.5"/><path d="M13 13l4 4"/>',
    plus: '<path d="M10 4v12M4 10h12"/>',
    back: '<path d="M12 5l-5 5 5 5"/>',
    check: '<path d="M4 10l4 4 8-8"/>',
    note: '<path d="M5 3h10v14H5z"/><path d="M8 7h4M8 10h4M8 13h2"/>',
  }[name] || '';
  return `<svg viewBox="0 0 20 20" width="16" height="16" fill="none" stroke="currentColor" stroke-width="1.6" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true">${p}</svg>`;
}

const mark = (size = 26) => `<img src="alvo-mark.svg" width="${size}" height="${size}" alt="" style="flex:none">`;
const avatar = (ch) => `<span class="a-brand-mark" style="background:var(--panel2);color:var(--dim)">${esc(ch)}</span>`;

function highlight(json) {
  return esc(json)
    .replace(/&quot;([^&]+?)&quot;(\s*:)/g, '<span class="a-code-key">"$1"</span>$2')
    .replace(/(:\s)&quot;([^&]*?)&quot;/g, '$1<span class="a-code-str">"$2"</span>');
}

/** A refusal, by slot, from the generated capability report. Never a copy held here. */
const refusal = (slot) => CAPABILITIES.refused.find((r) => r.slot === slot);
/** A warned block's consequence, served verbatim. */
const warning = (block) => CAPABILITIES.warned.find((w) => w.block === block)?.consequence ?? '';

/** The inert control a refused feature gets: present, disabled, carrying the framework's words. */
function refusedControl(slot, label, placeholder = '') {
  const r = refusal(slot);
  if (!r) return '';
  return `<div data-refused="${slot}">
    <div class="a-refused">
      <span class="a-label">${esc(label)}</span>
      <input class="a-input" placeholder="${esc(placeholder)}" disabled aria-describedby="why-${slot.replace(/\W/g, '-')}"></div>
    <div class="a-refused__reason" id="why-${slot.replace(/\W/g, '-')}">⚠ <span>${esc(r.consequence)}</span></div>
    <div class="a-refused__reason" style="color:var(--dim)"><span>→</span> <span>${esc(r.fix)}</span></div>
  </div>`;
}

/** A control that is real but not built in this drawing. Visibly inert, never silently dead. */
const inert = (label, why) =>
  `<button class="a-btn a-btn--sm" disabled title="${esc(why)}" aria-disabled="true">${esc(label)}<span class="a-notyet" style="margin-left:var(--space-2)">not drawn</span></button>`;

/* ==========================================================================
   Who is signed in

   §2.7: an operator carries ONE tenant, honoured the way TenantResolver honours an API key's —
   as a confirmation, never a choice. So there is no switcher, and the shell shows the tenant the
   operator acts in or says they carry none.
   §3.3: three independent access predicates, highest match wins; the bootstrap administrator is
   an admin whatever `access` says.
   ========================================================================== */

const LEVELS = [
  ['admin', 'Everything a developer may do, plus the settings surface: who holds which role, and who may reach the project at all.'],
  ['developer', 'Apply a descriptor and roll one back — what the backend is. Not who may reach it.'],
  ['viewer', 'Read the schema, the descriptor, the revisions and the capabilities, and simulate a policy.'],
];

const membershipOf = (id) => state.membership[id] ?? { roleNames: [], tenant: null, isDisabled: false };
const userById = (id) => USERS.find((u) => u.id === id) ?? USERS[0];
const me = () => userById(state.signedIn);

/** Assigned ∩ declared, plus `authenticated`. A name the descriptor does not declare is dropped
    silently — `AlvoIdentityContextResolver.Minted`. */
function mintedRoles(id) {
  const declared = new Set([...declaredRoles(wc.applied), ...BUILTIN_ROLES.map(([r]) => r)]);
  return [...new Set([...membershipOf(id).roleNames.filter((r) => declared.has(r)), 'authenticated'])];
}

const inertRolesOf = (id) => {
  const declared = new Set([...declaredRoles(wc.applied), ...BUILTIN_ROLES.map(([r]) => r)]);
  return membershipOf(id).roleNames.filter((r) => !declared.has(r));
};

const namesRole = (cel, role) => typeof cel === 'string' && cel.includes(`'${role}'`);

/** The highest level whose predicate any minted role satisfies, or null. Bootstrap is separate. */
function levelOf(id) {
  const block = accessBlock(wc.applied);
  const roles = mintedRoles(id);
  for (const [level] of LEVELS) {
    const predicate = block[level];
    if (predicate && roles.some((r) => namesRole(predicate, r))) return level;
  }
  return null;
}

const isBootstrap = (id) => userById(id).bootstrap;
const effectiveLevel = (id) => (isBootstrap(id) ? 'admin' : levelOf(id));
const myLevel = () => effectiveLevel(state.signedIn);
const myTenant = () => membershipOf(state.signedIn).tenant;

/** The caller `policy.js` answers for. */
const callerFor = (id) => ({ user: id, roles: mintedRoles(id), tenant: membershipOf(id).tenant });

const shortTenant = (id) => (id ? (TENANTS.find((t) => t.id === id)?.short ?? `${id.slice(0, 4)}…${id.slice(-4)}`) : null);

/* ==========================================================================
   Shell
   ========================================================================== */

const NAV = [
  { key: 'overview', label: 'Overview', icon: 'grid', route: '#/overview' },
  { key: 'schema', label: 'Schema', icon: 'schema', route: '#/schema' },
  { key: 'data', label: 'Data', icon: 'rows', route: '#/data' },
  { key: 'rules', label: 'Rules', icon: 'rule', route: '#/rules' },
  { key: 'access', label: 'Access', icon: 'shield', route: '#/access' },
  { key: 'history', label: 'Configuration history', icon: 'clock', route: '#/history' },
  { key: 'integrations', label: 'Integrations', icon: 'plug', route: '#/integrations' },
  { sep: true },
  { key: 'automations', label: 'Automations', icon: 'bolt', route: '#/automations', notYet: true },
  { key: 'functions', label: 'Functions', icon: 'fn', route: '#/functions', notYet: true },
  { sep: true },
  { key: 'settings', label: 'Settings', icon: 'cog', route: '#/settings' },
  { key: 'notes', label: 'Design notes', icon: 'note', route: '#/notes' },
];

const activeKey = () => (state.route.split('/')[1] || 'overview');

function sidebar() {
  const items = NAV.map((n) => {
    if (n.sep) return '<div class="a-nav-sep"></div>';
    const on = activeKey() === n.key ? ' a-nav-item--active' : '';
    const trail = n.notYet ? '<span class="a-notyet a-nav-item__trail">Not yet</span>' : '';
    return `<a class="a-nav-item${on}" href="${n.route}">${icon(n.icon)}<span>${n.label}</span>${trail}</a>`;
  }).join('');

  const unapplied = count();
  const user = me();
  const tenant = myTenant();

  return `<aside class="a-sidebar">
    <div class="a-brand">${mark(28)} Alvo</div>
    <button class="a-switcher" data-act="overlay" data-kind="projects">
      ${avatar('F')}
      <span><span class="a-switcher-name">${esc(wc.working.name)}</span>
      <span class="a-switcher-meta">revision ${wc.revision} · ${INFO.dataProvider}</span></span>
      ${unapplied ? `<span class="a-badge a-badge--accent" data-count="unapplied" style="margin-left:auto">${unapplied} unapplied</span>` : ''}
    </button>
    <nav class="a-nav">${items}</nav>
    <div style="margin-top:auto;display:flex;flex-direction:column;gap:var(--space-3)">
      <div class="a-row" data-act="overlay" data-kind="whoami" role="button" tabindex="0" style="cursor:pointer">${avatar(user.email.slice(0, 2).toUpperCase())}
        <span style="min-width:0"><span class="a-switcher-name">${esc(user.email)}</span>
        <span class="a-switcher-meta">${effectiveLevel(user.id) ?? 'no level'}${isBootstrap(user.id) ? ' · bootstrap' : ''} · ${tenant ? `tenant ${esc(shortTenant(tenant))}` : 'no tenant'}</span></span></div>
    </div>
  </aside>`;
}

function bottomnav() {
  return `<nav class="a-bottomnav">${NAV.filter((n) => !n.sep && !n.notYet).slice(0, 5)
    .map((n) => `<a class="a-bottomnav__item${activeKey() === n.key ? ' a-bottomnav__item--active' : ''}" href="${n.route}">${icon(n.icon)}${n.label.split(' ')[0]}</a>`).join('')}</nav>`;
}

function header(crumbs, actions = '') {
  return `<header class="a-header">
    <div class="a-row" style="min-width:0">
      ${crumbs.map((c, i) => (i === crumbs.length - 1
        ? `<span style="font-weight:var(--weight-medium)">${esc(c.label)}</span>`
        : `<a class="p-muted" href="${c.route}">${esc(c.label)}</a><span class="p-muted">/</span>`)).join('')}
    </div>
    <div class="a-row" style="margin-left:auto">${actions}</div>
  </header>`;
}

/** Past eight entities a bar of chips stops being scannable, so it becomes a typeahead. */
function entityBar(active, base, trailing = '') {
  const all = entities();
  if (all.length > 8) {
    return `<div class="a-entitybar">
      <input class="a-input" style="max-width:240px" placeholder="Jump to an entity…" data-act="entityfind" list="entity-names" value="${esc(active)}" aria-label="Jump to an entity">
      <datalist id="entity-names">${all.map((e) => `<option value="${e.name}">`).join('')}</datalist>
      <span class="p-muted">${all.length} entities</span>
      <span class="a-entitybar__add">${trailing}</span></div>`;
  }
  return `<div class="a-entitybar">
    ${all.map((e) => `<button class="a-entitybar__item${e.name === active ? ' a-entitybar__item--on' : ''}" data-act="go" data-route="${base}/${e.name}">
      ${e.name}<span class="a-entitybar__count">${e.fields.length}</span></button>`).join('')}
    <span class="a-entitybar__add">${trailing}</span>
  </div>`;
}

/** One bar, everywhere. Its primary action is Preview and never Apply. */
function pendingBar() {
  const n = count();
  if (!n) return '';
  const kinds = grouped().map((g) => `${g.rows.length} ${g.title.toLowerCase()}`).join(' · ');
  return `<div class="a-pending" data-pending>
    <span class="a-pending__count">${n} ${n === 1 ? 'change' : 'changes'} not applied</span>
    <span class="p-muted">${esc(kinds)} — nothing has reached the database.${touchesAccess() ? ' This working copy changes who may manage the project, so applying it needs <strong>admin</strong>.' : ''}</span>
    <span style="margin-left:auto" class="p-hstack">
      <button class="a-btn a-btn--sm a-btn--ghost" data-act="discard">Discard</button>
      <button class="a-btn a-btn--sm a-btn--primary" data-act="go" data-route="#/schema/preview">Preview changes</button></span>
  </div>`;
}

/** The descriptor pane: the WORKING copy, with the changed lines marked. */
function descriptorPane(scopeEntity) {
  const doc = scopeEntity
    ? { entities: { [scopeEntity]: wc.working.entities[scopeEntity] } }
    : wc.working;
  const base = scopeEntity
    ? { entities: { [scopeEntity]: wc.applied.entities?.[scopeEntity] } }
    : wc.applied;

  const lines = renderLines(doc, base);
  const body = lines.map((line) => {
    const cls = line.mark ? ` a-json__hit a-json__hit--${line.mark}` : '';
    const gutter = line.mark === 'added' ? '+' : line.mark === 'removed' ? '−' : line.mark ? '~' : ' ';
    const owned = line.pointer.match(/^\/entities\/[^/]+\/fields\/([^/]+)/);
    const selected = owned && owned[1] === state.selectedField ? ' a-json__sel' : '';
    return `<span class="a-json__line${cls}${selected}" data-pointer="${esc(line.pointer)}"><span class="a-json__g">${gutter}</span>${highlight(line.text)}</span>`;
  }).join('\n');

  const n = count();
  return `<div class="a-row">
      <span class="a-section-title" style="font-size:var(--text-sm)">Descriptor</span>
      <span class="p-muted" data-pane-header>${n ? `working copy · ${n} ${n === 1 ? 'change' : 'changes'} vs r${wc.revision}` : `as applied · r${wc.revision}`}</span>
      <button class="a-btn a-btn--sm a-btn--ghost" style="margin-left:auto" data-act="overlay" data-kind="export">Export</button></div>
    <pre class="a-json" data-descriptor>${body}</pre>`;
}

/* ==========================================================================
   Overview
   ========================================================================== */

function screenOverview() {
  /* The warned panel is `warned` INTERSECTED with the blocks this descriptor declares.
     `CapabilityReport.Project()` projects all five; `UnhonouredSubsystems.DeclaredBy` is the
     predicate that narrows them, and a block declined by value is not a declaration. */
  const declared = CAPABILITIES.warned.filter((w) => declaresBlock(w.block));

  const warnedPanel = declared.length
    ? declared.map((w) => `<div class="a-row" style="align-items:flex-start;padding:var(--space-3) var(--space-5);border-bottom:1px solid var(--border)">
        <code class="a-mono" style="flex:none;width:122px;font-size:var(--text-xs)">${esc(w.block)}</code>
        <span class="p-muted" style="flex:1">${esc(w.consequence)}</span>
        <span class="a-notyet">Not yet</span></div>`).join('')
    : `<div class="a-empty"><span class="a-empty__title">This descriptor declares none of them</span>
        <span class="a-empty__body">Five blocks apply and then do nothing in this build — <code class="a-mono">${CAPABILITIES.warned.map((w) => w.block).join('</code>, <code class="a-mono">')}</code>. Yours declares none, so nothing here is silently inert.
        <strong>This panel is the only place you will be told.</strong> A runtime apply writes no warning line at all: the one caller of the warning is the boot path (#83).</span></div>`;

  const list = entities();
  const ruleCount = list.reduce((n, e) => n + Object.values(e.rules).filter(Boolean).length, 0);
  const tenant = myTenant();
  const scoped = list.filter((e) => e.tenancy === 'scoped');

  const cards = [
    ['Entities', list.length, 'tables Alvo created and keeps in step'],
    ['Records', tenant ? num(scoped.reduce((n, e) => n + ROW_COUNTS[e.name] ?? 0, 0) || 0) : '—',
      tenant ? `in your tenant, ${esc(shortTenant(tenant))}` : 'you carry no tenant, so no scoped entity is readable'],
    ['Rules', `${ruleCount}`, `of ${list.length * 5} operations guarded`],
    ['Revision', `r${wc.revision}`, `${wc.history.length} in the history`],
  ].map(([k, v, sub]) => `<div class="a-card">
      <span class="a-label">${k}</span>
      <span style="font-size:var(--text-2xl);font-weight:var(--weight-bold);font-variant-numeric:tabular-nums">${v}</span>
      <span class="p-muted">${sub}</span></div>`).join('');

  if (!list.length) return screenOverviewEmpty();

  return `${header([{ label: 'Overview' }], '<button class="a-btn" data-act="go" data-route="#/schema/transfer">Export descriptor</button>')}
  <div class="a-content"><div class="a-stack">
    <div class="p-between">
      <div><h1 class="a-page-title">${esc(wc.working.name)}</h1>
        <p class="p-muted p-tight" style="max-width:64ch">${esc(wc.working.description ?? '')}</p></div>
      <div class="p-hstack">
        <span class="a-badge a-badge--ok"><span class="a-dot"></span> Revision ${wc.revision} applied</span>
        <span class="a-badge" title="ManagementInfo.DataProvider — the registered IAlvoData implementation, never an engine name">${esc(INFO.dataProvider)}</span>
        ${tenancyEnabled() ? '<span class="a-badge">multi-tenant</span>' : ''}</div>
    </div>

    <div class="a-cards">${cards}</div>

    ${count() ? `<div class="a-row" style="padding:var(--space-4);border:1px solid var(--accentBorder);border-radius:var(--radius-md);background:var(--accentSoft)">
      <span style="font-weight:var(--weight-medium)">${count()} ${count() === 1 ? 'change is' : 'changes are'} waiting</span>
      <span class="p-muted">Edited and not applied. Nothing has reached the database.</span>
      <button class="a-btn a-btn--sm a-btn--primary" style="margin-left:auto" data-act="go" data-route="#/schema/preview">Review them</button>
    </div>` : ''}

    <div class="a-split">
      <div class="a-stack">
        <div class="a-panel">
          <div class="a-section"><span class="a-section-title">Your entities</span>
            <a class="a-btn a-btn--sm a-btn--ghost" style="margin-left:auto" href="#/schema">Open schema</a></div>
          ${list.map((e) => `<div class="a-row" style="align-items:flex-start;padding:var(--space-4) var(--space-5);border-bottom:1px solid var(--border)">
            <span style="flex:1;min-width:0">
              <a style="font-family:var(--font-mono);font-size:var(--text-sm);font-weight:var(--weight-medium)" href="#/schema/${e.name}">${e.name}</a>
              <span class="a-switcher-meta">${e.fields.length} fields · ${num(ROW_COUNTS[e.name] ?? 0)} records · ${e.tenancy}</span></span>
            <span class="p-hstack" style="flex:none">
              <a class="a-btn a-btn--sm a-btn--ghost" href="#/data/${e.name}">Browse</a>
              <a class="a-btn a-btn--sm a-btn--ghost" href="#/schema/${e.name}">Edit</a></span>
          </div>`).join('')}
        </div>

        <div class="a-panel">
          <div class="a-section"><span class="a-section-title">Declared, and not running yet</span>
            <span class="a-section-sub">From <code class="a-mono">GET ${mgmt('/projects/{project}/capabilities')}</code>, intersected with what this descriptor declares. The wording is the server's, verbatim.</span></div>
          ${warnedPanel}
        </div>
      </div>

      <div class="a-stack">
        <div class="a-panel">
          <div class="a-section"><span class="a-section-title">What this build honours</span></div>
          <div style="padding:var(--space-4) var(--space-5)" class="p-hstack">
            ${CAPABILITIES.honoured.map((h) => `<span class="a-badge a-badge--ok">${esc(h)}</span>`).join('')}
          </div>
          <div style="padding:0 var(--space-5) var(--space-4);color:var(--faint);font-size:var(--text-xs)">
            A written list, not a derivation — nothing in the framework enumerates what it <em>does</em> honour, and a test holds this disjoint from the warned table. <code class="a-mono">branding</code> is in neither list, deliberately.
          </div>
        </div>

        <div class="a-panel">
          <div class="a-section"><span class="a-section-title">Recent changes</span>
            <a class="a-btn a-btn--sm a-btn--ghost" style="margin-left:auto" href="#/history">All</a></div>
          ${wc.history.slice(0, 4).map((r) => `<div class="a-row" style="align-items:flex-start;padding:var(--space-3) var(--space-5);border-bottom:1px solid var(--border)">
            <span class="a-badge${r.rolledBackFrom ? ' a-badge--warn' : ''}" style="flex:none">r${r.revision}</span>
            <span style="flex:1;min-width:0"><span style="font-size:var(--text-sm)">${esc(r.reason)}</span>
              <span class="a-switcher-meta">${esc(r.author)} · ${r.at}</span></span></div>`).join('')}
        </div>
      </div>
    </div>
  </div></div>`;
}

function screenOverviewEmpty() {
  return `${header([{ label: 'Overview' }])}
  <div class="a-content"><div class="a-stack">
    <div><h1 class="a-page-title">${esc(wc.working.name)}</h1></div>
    <div class="a-empty">
      <span class="a-empty__title">Nothing is modelled yet</span>
      <span class="a-empty__body">A project is one descriptor, and yours declares no entities. An entity becomes a table, a REST resource and a set of rules the moment you apply it.</span>
      <span class="p-hstack">
        <a class="a-btn a-btn--primary" href="#/schema">Model the first entity</a>
        <a class="a-btn" href="#/schema/transfer">I already have a descriptor</a></span>
    </div>
  </div></div>`;
}

/** The management route prefix, as the generated table spells it. */
const MGMT_PREFIX = '/management';
const mgmt = (path) => `${MGMT_PREFIX}${path}`;

/* ==========================================================================
   Schema — the list, and the model drawn
   ========================================================================== */

function screenSchemaList() {
  const list = entities();

  if (!list.length) {
    return `${header([{ label: 'Schema' }], `<button class="a-btn a-btn--primary" data-act="overlay" data-kind="new-entity">${icon('plus')} New entity</button>`)}
    <div class="a-content"><div class="a-stack">
      <div><h1 class="a-page-title">Entities</h1></div>
      <div class="a-empty">
        <span class="a-empty__title">No entities yet</span>
        <span class="a-empty__body">Start with the thing your backend is actually about — <code class="a-mono">invoices</code>, <code class="a-mono">customers</code>, <code class="a-mono">work_orders</code>. Name it in the plural. Alvo turns it into a table, five REST routes and a set of rules that default to refusing everyone.</span>
        <span class="p-hstack">
          <button class="a-btn a-btn--primary" data-act="overlay" data-kind="new-entity">${icon('plus')} New entity</button>
          <a class="a-btn" href="#/schema/transfer">Import a descriptor</a></span>
      </div>
      ${pendingBar()}
    </div></div>`;
  }

  const rows = list.map((e) => `<tr data-act="go" data-route="#/schema/${e.name}" tabindex="0" style="cursor:pointer">
      <td><span style="font-family:var(--font-mono);font-size:var(--text-sm);font-weight:var(--weight-medium)">${e.name}</span>
        <div class="p-muted" style="max-width:52ch">${esc(e.description.slice(0, 92))}${e.description.length > 92 ? '…' : ''}</div></td>
      <td><span class="a-badge${e.tenancy === 'global' ? '' : ' a-badge--accent'}">${e.tenancy}</span></td>
      <td class="a-num">${e.fields.length}</td>
      <td class="a-num">${num(ROW_COUNTS[e.name] ?? 0)}</td>
      <td>${hookCount(e) ? `<span class="a-badge">${hookCount(e)} hooks</span>` : '<span class="p-muted">—</span>'}</td>
      <td>${e.fields.filter((f) => f.type === 'ref').map((f) => `<span class="a-badge">→ ${f.entity}</span>`).join(' ') || '<span class="p-muted">—</span>'}</td>
    </tr>`).join('');

  return `${header([{ label: 'Schema' }], `
      <button class="a-btn p-hide-sm" data-act="go" data-route="#/schema/transfer">Import / export</button>
      <button class="a-btn a-btn--primary" data-act="overlay" data-kind="new-entity">${icon('plus')} New entity</button>`)}
  <div class="a-content"><div class="a-stack">
    <div><h1 class="a-page-title">Entities</h1>
      <p class="p-muted p-tight" style="max-width:70ch">Each one is a table Alvo created and keeps in step with the descriptor. Open one to change its fields, connect it to another entity, and decide who may read or write it.</p></div>

    <div class="a-grid-wrap">
      <table class="a-grid">
        <thead><tr><th>Entity</th><th>Tenancy</th><th class="a-num">Fields</th><th class="a-num">Records</th><th>On write</th><th>Points at</th></tr></thead>
        <tbody>${rows}</tbody></table>
      ${list.map((e) => `<div class="a-row-card" data-act="go" data-route="#/schema/${e.name}" tabindex="0">
        <div class="a-row-card__head"><span style="font-family:var(--font-mono)">${e.name}</span><span class="a-badge">${e.tenancy}</span></div>
        <div class="a-row-card__meta"><span>${e.fields.length} fields</span><span>${num(ROW_COUNTS[e.name] ?? 0)} records</span></div></div>`).join('')}
    </div>

    <div class="a-panel">
      <div class="a-section"><span class="a-section-title">The model</span>
        <span class="a-section-sub">Every relation is many-to-one and starts at the <code class="a-mono">ref</code> field that makes it. The word beside the arrow is what a delete on the other side does. Click a box to open it.</span></div>
      ${entityMap()}
      <div class="p-note" style="margin:0 var(--space-5) var(--space-5)"><span class="p-note__tag">read only</span>
        <span>You cannot draw a relation here. A dragged line would have nowhere to be written — there is no relation object in the descriptor, only the <code class="a-mono">ref</code> field, which is added in the entity's own editor.</span></div>
    </div>
    ${pendingBar()}
  </div></div>`;
}

const hookCount = (e) => Object.values(e.hooks ?? {}).reduce((n, list) => n + (list?.length ?? 0), 0);

/* The model, drawn. Past a handful of entities the whole graph is a 3,000 px picture nobody reads,
   so it draws the focused entity and one hop in each direction. */
function entityMap(focus = null) {
  const W = 256, HEAD = 42, ROW = 19, PAD = 12, COL = 132, GAP = 38;
  let list = entities();

  if (focus || list.length > 6) {
    const centre = focus ?? list[0].name;
    const keep = new Set([centre]);
    for (const e of list) {
      for (const f of e.fields) {
        if (f.type !== 'ref') continue;
        if (e.name === centre) keep.add(f.entity);
        if (f.entity === centre) keep.add(e.name);
      }
    }
    list = list.filter((e) => keep.has(e.name));
  }

  if (!list.length) return '<div class="a-empty"><span class="a-empty__body">Nothing to draw yet.</span></div>';

  const byName = Object.fromEntries(list.map((e) => [e.name, e]));
  const depth = (e, seen = new Set()) => {
    if (!e || seen.has(e.name)) return 0;
    seen.add(e.name);
    const refs = e.fields.filter((f) => f.type === 'ref' && byName[f.entity]);
    return refs.length ? 1 + Math.max(...refs.map((f) => depth(byName[f.entity], seen))) : 0;
  };

  const cols = [];
  list.forEach((e) => {
    const d = depth(e);
    (cols[d] = cols[d] || []).push(e);
  });

  const box = {};
  let maxY = 0;
  cols.forEach((col, ci) => {
    let y = 0;
    (col || []).forEach((e) => {
      const refs = e.fields.filter((f) => f.type === 'ref');
      const rest = e.fields.filter((f) => f.type !== 'ref');
      const shown = [...rest.slice(0, 6), ...refs];
      const hidden = rest.length - Math.min(rest.length, 6);
      const h = HEAD + shown.length * ROW + PAD + (hidden > 0 ? 14 : 0);
      box[e.name] = { x: ci * (W + COL), y, h, shown, more: hidden };
      y += h + GAP;
      maxY = Math.max(maxY, y);
    });
  });

  const width = cols.length * W + Math.max(0, cols.length - 1) * COL;
  const height = Math.max(40, maxY - GAP);

  const boxes = list.map((e) => {
    const b = box[e.name];
    return `<g class="a-map__box" data-act="go" data-route="#/schema/${e.name}">
      <rect class="a-map__plate" x="${b.x}" y="${b.y}" width="${W}" height="${b.h}" rx="10"/>
      <path class="a-map__head" d="M${b.x} ${b.y + 10}a10 10 0 0 1 10-10h${W - 20}a10 10 0 0 1 10 10v${HEAD - 10}h-${W}z"/>
      <text class="a-map__name" x="${b.x + 12}" y="${b.y + 17}">${e.name}</text>
      <text class="a-map__meta" x="${b.x + 12}" y="${b.y + 30}">${e.tenancy} · ${e.fields.length} fields · ${num(ROW_COUNTS[e.name] ?? 0)} records</text>
      ${b.shown.map((f, i) => {
        const ty = b.y + HEAD + i * ROW + 12;
        const isRef = f.type === 'ref';
        return `<text class="a-map__field${isRef ? ' a-map__field--ref' : ''}" x="${b.x + 12}" y="${ty}">${f.name}</text>
          <text class="a-map__type" x="${b.x + W - 12}" y="${ty}" text-anchor="end">${isRef ? '→ ' + f.entity + (f.onDelete ? ' · ' + f.onDelete : '') : f.type}</text>`;
      }).join('')}
      ${b.more > 0 ? `<text class="a-map__type" x="${b.x + 12}" y="${b.y + HEAD + b.shown.length * ROW + 8}">+${b.more} more</text>` : ''}
    </g>`;
  }).join('');

  const wires = list.flatMap((e) => e.fields.filter((f) => f.type === 'ref').map((f) => {
    const from = box[e.name];
    const to = box[f.entity];
    if (!to) return '';
    const i = from.shown.findIndex((x) => x.name === f.name);
    const y1 = from.y + HEAD + (i < 0 ? from.shown.length - 1 : i) * ROW + 8;
    const y2 = to.y + 19;
    const x1 = from.x;
    const x2 = to.x + W;
    const mid = (x1 + x2) / 2;
    return `<path class="a-map__wire" d="M${x1} ${y1}C${mid} ${y1} ${mid} ${y2} ${x2} ${y2}"/>
      <circle class="a-map__dot" cx="${x1}" cy="${y1}" r="3"/>`;
  })).join('');

  const scoped = focus || entities().length > 6
    ? `<div class="p-muted" style="padding:0 var(--space-5) var(--space-4)">Showing ${list.length} of ${entities().length} — ${esc(focus ?? entities()[0].name)} and one hop in each direction. The whole graph at this size is a picture nobody reads.</div>`
    : '';

  return `${scoped}<div class="a-map">
    <svg viewBox="-6 -6 ${width + 12} ${height + 12}" width="${width}" height="${height}" role="img"
      aria-label="Entity relationship map. ${list.map((e) => `${e.name} points at ${e.fields.filter((f) => f.type === 'ref').map((f) => f.entity).join(' and ') || 'nothing'}`).join('; ')}.">
      ${wires}${boxes}
    </svg>
  </div>`;
}

/* ==========================================================================
   The entity editor

   The field editor lives in the RIGHT COLUMN, above the descriptor pane it annotates — the drawer
   it used to open in was 560 px over a 520 px aside, so "selecting a field marks the lines it
   owns" was true only after you closed the thing that made the claim. Below 1100 px there is one
   column and the editor is a full-width panel in it.
   ========================================================================== */

const TABS = [
  ['fields', 'Fields'], ['relationships', 'Relationships'], ['rules', 'Rules'],
  ['hooks', 'On write'], ['indexes', 'Indexes'], ['api', 'API'],
];

function screenEntity(name) {
  const e = entityView(name);
  if (!e) return screenSchemaList();
  const tab = state.tab;

  const tabs = TABS.map(([t, label]) => {
    const n = t === 'fields' ? e.fields.length : t === 'hooks' ? hookCount(e) : t === 'indexes' ? e.indexes.length : 0;
    return `<button class="a-tab${t === tab ? ' a-tab--active' : ''}" data-act="tab" data-tab="${t}">${label}${n ? ` <span class="p-muted">${n}</span>` : ''}</button>`;
  }).join('');

  const body = {
    fields: fieldsTab, relationships: relationshipsTab, rules: rulesList,
    hooks: hooksTab, indexes: indexesTab, api: apiTab,
  }[tab](e);

  const wide = tab === 'api' || tab === 'hooks';
  const panel = `<div class="a-panel"><div class="a-tabs">${tabs}</div>${body}</div>`;

  const aside = `<div class="a-split__aside">
      ${state.selectedField !== null && tab === 'fields' ? fieldEditor(e, state.selectedField) : ''}
      ${descriptorPane(e.name)}
    </div>`;

  return `${header([{ label: 'Schema', route: '#/schema' }, { label: name }], `
      <button class="a-btn p-hide-sm" data-act="overlay" data-kind="export">Export</button>
      <button class="a-btn a-btn--primary" data-act="go" data-route="#/schema/preview">Preview${count() ? ` (${count()})` : ''}</button>`)}
  <div class="a-content"><div class="a-stack">
    ${entityBar(name, '#/schema', `<button class="a-btn a-btn--sm" data-act="overlay" data-kind="new-entity">${icon('plus')} Entity</button>`)}

    <div class="p-between">
      <div><h1 class="a-page-title" style="font-family:var(--font-mono)">${name}</h1>
        <p class="p-muted p-tight" style="max-width:66ch">${esc(e.description)}</p></div>
      <div class="p-hstack">
        <span class="a-badge${e.tenancy === 'global' ? '' : ' a-badge--accent'}">${e.tenancy}</span>
        ${e.audit ? '<span class="a-badge a-badge--ok" title="audit: true — created_at, created_by, updated_at, updated_by, and a version every write can be made conditional on">audited</span>' : ''}
        <a class="a-btn a-btn--sm" href="#/data/${name}">Browse ${num(ROW_COUNTS[name] ?? 0)} records</a></div>
    </div>

    ${wide ? `${panel}${pendingBar()}`
      : `<div class="a-split a-split--wide">
        <div class="a-stack">${panel}${pendingBar()}</div>
        ${aside}
      </div>`}
  </div></div>`;
}

function fieldsTab(e) {
  const filter = state.filterFields.trim().toLowerCase();
  const shown = filter ? e.fields.filter((f) => f.name.includes(filter) || f.type.includes(filter)) : e.fields;
  const changedFields = new Set(changes()
    .map((c) => c.pointer.match(/^\/entities\/([^/]+)\/fields\/([^/]+)/))
    .filter((m) => m && m[1] === e.name)
    .map((m) => m[2]));

  const rows = shown.map((f) => `<button class="a-fieldrow${state.selectedField === f.name ? ' a-fieldrow--active' : ''}" data-act="field" data-field="${f.name}">
      <span class="a-fieldrow__name">${f.name}${changedFields.has(f.name) ? ' <span class="a-dot" data-changed title="Edited, not applied"></span>' : ''}</span>
      <span class="a-fieldrow__type">${typeLabel(f)}</span>
      <span class="a-fieldrow__flags">${flagBadges(f)}</span>
      <span class="a-fieldrow__drag" aria-hidden="true">⋮⋮</span></button>`).join('');

  const managed = ['id'];
  if (e.audit) managed.push('created_at', 'created_by', 'updated_at', 'updated_by');

  return `<div class="a-toolbar">
      <input class="a-input" style="max-width:240px" placeholder="Filter fields" aria-label="Filter fields" data-act="fieldfilter" value="${esc(state.filterFields)}">
      <span class="p-muted">Selecting a field opens it beside the descriptor and marks the lines it owns.</span>
      <span style="margin-left:auto" class="p-hstack">
        <button class="a-btn a-btn--sm a-btn--primary" data-act="field" data-field="__new">${icon('plus')} Add field</button></span>
    </div>${rows || '<div class="a-empty"><span class="a-empty__body">No field matches that filter.</span></div>'}
    <div class="a-row" style="padding:var(--space-3) var(--space-4);color:var(--faint);font-size:var(--text-xs)">
      <span>Alvo maintains <code class="a-mono">${managed.join('</code>, <code class="a-mono">')}</code> itself. ${e.audit
        ? 'The four audit columns exist because this entity sets <code class="a-mono">audit: true</code>; without it only <code class="a-mono">id</code> is added.'
        : 'Only <code class="a-mono">id</code> is unconditional — <code class="a-mono">created_at</code> and the rest arrive with <code class="a-mono">audit: true</code>.'}</span>
    </div>`;
}

const typeLabel = (f) => (f.type === 'ref' ? `ref → ${f.entity}` : f.type);

function flagBadges(f) {
  const out = [];
  if (f.computed) out.push('<span class="a-badge a-badge--accent">computed</span>');
  if (f.rollup) out.push(`<span class="a-badge a-badge--accent">${f.rollup.op} of ${f.rollup.from}</span>`);
  if (f.required) out.push('<span class="a-badge">required</span>');
  if (f.unique) out.push('<span class="a-badge">unique</span>');
  if (f.hidden) out.push('<span class="a-badge a-badge--warn">hidden</span>');
  if (f.readOnly) out.push('<span class="a-badge">read only</span>');
  if (f.index) out.push('<span class="a-badge">indexed</span>');
  if (f.format) out.push(`<span class="a-badge">${esc(f.format)}</span>`);
  if (f.maxLength) out.push(`<span class="a-badge">max ${f.maxLength}</span>`);
  if (f.precision) out.push(`<span class="a-badge">${f.precision},${f.scale}</span>`);
  if (f.values) out.push(`<span class="a-badge">${f.values.length} values</span>`);
  if (f.onDelete) out.push(`<span class="a-badge">on delete ${esc(f.onDelete)}</span>`);
  return out.join(' ');
}

/* --- The field editor, in the column beside the pane ---------------------- */

function fieldEditor(e, name) {
  const isNew = name === '__new';
  const f = isNew ? (state.draftField ?? { name: '', type: 'string' }) : e.fields.find((x) => x.name === name);
  if (!f) return '';
  const derived = !!(f.computed || f.rollup);
  const facets = SCHEMA_FACETS;

  const types = `<div class="a-typegrid">${facets.types.map((t) => `<button class="a-typechip${t === f.type ? ' a-typechip--on' : ''}" data-act="settype" data-field="${esc(f.name)}" data-type="${t}" type="button">${t}</button>`).join('')}</div>
    <span class="a-typehint">${esc(TYPE_HINTS[f.type] ?? '')}</span>`;

  /* The warning fires on a type that has CHANGED against the applied descriptor, not permanently
     on every field that happens to carry a facet. */
  const appliedField = wc.applied.entities?.[e.name]?.fields?.[f.name];
  const typeChanged = appliedField && appliedField.type !== f.type;
  const lost = typeChanged
    ? [...(facets.needs[appliedField.type] ?? []), ...(facets.optional[appliedField.type] ?? [])].filter((k) => appliedField[k] !== undefined)
    : [];
  const typeWarn = typeChanged
    ? `<div class="a-typewarn" data-typewarn>⚠ <span>Changed from <code class="a-mono">${appliedField.type}</code>. The schema allows ${lost.length ? `<code class="a-mono">${lost.join('</code>, <code class="a-mono">')}</code> only on <code class="a-mono">${appliedField.type}</code>, so ${lost.length > 1 ? 'those are' : 'that is'} dropped, and ` : ''}the column is rewritten over ${num(ROW_COUNTS[e.name] ?? 0)} rows. Preview will ask you to type <code class="a-mono">${e.name}</code> before it runs.</span></div>`
    : '';

  const needed = (body) => `<div class="a-needed">
      <span class="a-needed__head">${icon('check')} Needed for ${/^[aeiou]/.test(f.type) ? 'an' : 'a'} ${f.type}</span>${body}</div>`;

  const formats = [...facets.builtInFormats, ...declaredFormats()];

  const facetControls = () => {
    if (f.type === 'string') return `<div class="a-field"><span class="a-label">Longest value allowed<span class="a-label__hint">Becomes the column width. Widening one later is safe; narrowing it is not.</span></span>
        <input class="a-input" value="${f.maxLength ?? ''}" placeholder="160" data-act="setfacet" data-field="${esc(f.name)}" data-key="maxLength" data-kind="int"></div>
      <div class="a-field"><span class="a-label">Must look like<span class="a-label__hint">Three built-ins plus whatever <code class="a-mono">formats</code> declares. An unknown name is refused fail-fast at apply, not by the schema.</span></span>
        <div class="p-hstack">${['none', ...formats].map((o) => `<button class="a-preset${(f.format ?? 'none') === o ? ' a-preset--on' : ''}" type="button" data-act="setfacet" data-field="${esc(f.name)}" data-key="format" data-value="${o === 'none' ? '' : o}">${o}</button>`).join('')}</div></div>`;

    if (f.type === 'decimal') return needed(`
      <div class="a-row" style="align-items:flex-start;gap:var(--space-4)">
        <div class="a-field" style="flex:1"><span class="a-label">Total digits</span>
          <input class="a-input" value="${f.precision ?? 10}" data-act="setfacet" data-field="${esc(f.name)}" data-key="precision" data-kind="int"></div>
        <div class="a-field" style="flex:1"><span class="a-label">After the point</span>
          <input class="a-input" value="${f.scale ?? 2}" data-act="setfacet" data-field="${esc(f.name)}" data-key="scale" data-kind="int"></div></div>
      <span class="p-muted">Total counts every digit, not only the ones after the point — <code class="a-mono">10,2</code> holds up to 99,999,999.99. Both are required: a decimal without them cannot be applied.</span>`);

    if (f.type === 'enum') return needed(`
      <div class="a-field"><span class="a-label">Allowed values</span>
        <div class="p-hstack">${(f.values ?? []).map((v) => `<span class="a-chip">${esc(v)} <button data-act="enumremove" data-field="${esc(f.name)}" data-value="${esc(v)}" aria-label="Remove ${esc(v)}" type="button">✕</button></span>`).join('')}
        <input class="a-input" style="max-width:150px" placeholder="+ add a value" data-act="enumadd" data-field="${esc(f.name)}" aria-label="Add a value"></div></div>
      <span class="p-muted">${(f.values ?? []).length ? 'Removing a value is refused while a record still holds it — the check runs at apply, against your data.' : '<strong>At least one is required.</strong> An enum with no values is a descriptor the apply refuses, so this field cannot be added until it has one. Type a value and press comma.'}</span>`);

    if (f.type === 'ref') return needed(`
      <div class="a-field"><span class="a-label">Points at</span>
        <div class="p-hstack">${entities().filter((x) => x.name !== e.name).map((x) => `<button class="a-preset${x.name === f.entity ? ' a-preset--on' : ''}" type="button" data-act="setfacet" data-field="${esc(f.name)}" data-key="entity" data-value="${x.name}">${x.name}</button>`).join('')}</div></div>
      <div class="a-field"><span class="a-label">When that record is deleted<span class="a-label__hint">Optional. Without it the delete is refused, which is the safe default.</span></span>
        <div class="p-hstack">${facets.onDelete.map((o) => `<button class="a-preset${f.onDelete === o ? ' a-preset--on' : ''}" type="button" data-act="setfacet" data-field="${esc(f.name)}" data-key="onDelete" data-value="${o}">${ON_DELETE_WORDS[o]}</button>`).join('')}</div></div>`);

    return '';
  };

  const flags = [
    ['Must have a value', 'required', f.required, 'required'],
    ['No two records share it', 'unique', f.unique, 'unique'],
    ['May be null', 'nullable', f.nullable, 'nullable'],
    ['Alvo maintains it, callers cannot write it', 'readOnly', f.readOnly === true, 'readOnly'],
    ['Never returned, and not nameable in a filter', 'hidden', f.hidden === true, 'hidden'],
    ['Indexed on its own', 'index', f.index, 'index'],
  ];

  return `<div class="a-panel a-form" data-field-editor style="padding:var(--space-4);gap:var(--space-4);margin-bottom:var(--space-4)">
    <div class="p-between">
      <div><span class="a-section-title">${isNew ? 'New field' : esc(f.name)}</span>
        <p class="p-muted p-tight">on <code class="a-mono">${e.name}</code></p></div>
      <button class="a-btn a-btn--sm a-btn--ghost" data-act="closefield">Close</button>
    </div>

    <div class="a-field"><span class="a-label">Field name<span class="a-label__hint">Renaming keeps the data — Alvo writes <code class="a-mono">renamedFrom</code> and the migration moves the column.</span></span>
      <input class="a-input" value="${esc(f.name)}" placeholder="scheduled_for" style="font-family:var(--font-mono)" data-act="setname" data-field="${esc(f.name)}"></div>

    <div class="a-field"><span class="a-label">Type</span>${types}${typeWarn}</div>

    <div class="a-field"><span class="a-label">What it holds<span class="a-label__hint">Becomes this field's description in the generated OpenAPI document.</span></span>
      <textarea class="a-textarea" style="min-height:56px" data-act="setfacet" data-field="${esc(f.name)}" data-key="description">${esc(f.description ?? '')}</textarea></div>

    ${facetControls()}

    ${derived ? `<div class="a-field"><span class="a-label">Alvo maintains this value<span class="a-label__hint">Nobody writes it through the API, and it is kept in the same transaction as the change that moves it. A derived field has no constraints panel at all — nobody writes it, so "required" and "unique" have nothing to mean.</span></span>
        ${f.computed
          ? `<div class="a-field"><span class="a-label">Derived from this row<span class="a-label__hint">The Computed profile: this row's own fields, arithmetic and a ternary. No <code class="a-mono">@user</code>, no <code class="a-mono">old.</code>/<code class="a-mono">new.</code>, no <code class="a-mono">in</code>.</span></span>
              <input class="a-input" style="font-family:var(--font-mono)" value="${esc(f.computed)}" data-act="setfacet" data-field="${esc(f.name)}" data-key="computed"></div>`
          : `<div class="a-field"><span class="a-label">Count over which entity<span class="a-label__hint">A child entity that points at this one with a <code class="a-mono">ref</code> field. A rollup naming an entity with no ref back is refused at apply.</span></span>
              <div class="p-hstack">${entities().filter((x) => x.name !== e.name).map((x) => {
                const points = x.fields.some((ff) => ff.type === 'ref' && ff.entity === e.name);
                return `<button class="a-preset${f.rollup.from === x.name ? ' a-preset--on' : ''}" type="button" data-act="rollupfrom" data-field="${esc(f.name)}" data-value="${x.name}"${points ? '' : ` disabled aria-disabled="true" title="${x.name} has no ref field pointing at ${e.name}, so a rollup over it is refused at apply"`}>${x.name}</button>`;
              }).join('')}</div></div>
             <div class="a-field"><span class="a-label">Aggregate</span>
              <div class="p-hstack">${SCHEMA_FACETS.rollupOps.map((op) => `<button class="a-preset${f.rollup.op === op ? ' a-preset--on' : ''}" type="button" data-act="rollupop" data-field="${esc(f.name)}" data-value="${op}">${op}</button>`).join('')}</div>
              <span class="a-label__hint">${f.rollup.op === 'count' ? 'Counts the child records. The only operation that needs no field.' : `Over <code class="a-mono">${esc(f.rollup.field ?? '—')}</code> on <code class="a-mono">${esc(f.rollup.from)}</code>. Every operation but <code class="a-mono">count</code> requires one.`}</span></div>
             <div class="a-readout"><span class="a-readout__tag">descriptor</span><span>${f.rollup.op}(${f.rollup.from}${f.rollup.field ? '.' + f.rollup.field : ''})</span></div>
             ${refusedControl('rollup.where', 'Only count some child records', "status != 'cancelled'")}`}
        <button class="a-btn a-btn--sm a-btn--ghost" data-act="underive" data-field="${esc(f.name)}">Make it an ordinary field</button>
      </div>`
      : `<div class="a-panel" style="padding:var(--space-4);display:flex;flex-direction:column;gap:var(--space-4)">
      ${flags.map(([label, k, on]) => `
        <label class="a-row" style="gap:var(--space-3)">
          <span class="a-toggle${on ? ' a-toggle--on' : ''}" role="switch" tabindex="0" aria-checked="${!!on}" data-act="toggleflag" data-field="${esc(f.name)}" data-key="${k}"></span>
          <span><span style="font-size:var(--text-sm)">${label}</span><span class="a-switcher-meta">${k}</span></span></label>`).join('')}
      <div class="p-hstack">
        <button class="a-btn a-btn--sm" data-act="derive" data-field="${esc(f.name)}" data-kind="computed">Make it computed</button>
        <button class="a-btn a-btn--sm" data-act="derive" data-field="${esc(f.name)}" data-kind="rollup">Make it a rollup</button></div>
      <span class="p-muted"><code class="a-mono">hidden</code> restricts reading, <code class="a-mono">readOnly</code> restricts writing. A hidden field is still writable — and a <em>required</em> hidden one is the single case where a hidden field's name is published, in the write schemas only.</span>
    </div>`}

    <details class="a-disclose">
      <summary>Two constraints this build refuses</summary>
      <div class="a-disclose__body" style="display:flex;flex-direction:column;gap:var(--space-4)">
        ${refusedControl('field.default', 'Value when none is given', 'scheduled')}
        ${refusedControl('field.validation', 'Custom validation rule', 'value.matches(…)')}
        <span class="p-muted">Refused at apply rather than ignored, so a descriptor declaring one is rejected instead of quietly storing the wrong value. The control is here, and inert, so you can see the decision was made rather than forgotten.</span>
      </div>
    </details>

    ${state.fieldError && isNew ? `<div class="a-error" data-field-error>
      <span class="a-error__title">Not added</span>
      <span class="a-error__detail">${esc(state.fieldError)}</span></div>` : ''}

    <div class="a-row">
      ${isNew ? '' : `<button class="a-btn a-btn--danger a-btn--sm" data-act="removefield" data-field="${esc(f.name)}">Remove field</button>`}
      <span style="margin-left:auto" class="p-hstack">
        ${isNew ? `<button class="a-btn a-btn--primary" data-act="addfield">Add to the model</button>` : '<span class="p-muted">Edits land in the working copy as you make them.</span>'}</span>
    </div>
  </div>`;
}

const TYPE_HINTS = {
  string: 'One line of text, with a length limit you set.',
  text: 'Long form text. No length limit, and not something to sort by.',
  integer: 'A whole number. Counts, priorities, quantities.',
  decimal: 'A number with a fixed number of decimal places. Money belongs here, never in a float.',
  boolean: 'True or false, and nothing between.',
  date: 'A day, with no time and no zone.',
  datetime: 'A moment, stored in UTC and returned in UTC.',
  uuid: 'An identifier from somewhere else — a user id, an external key. Alvo does not know what it points at.',
  json: 'Anything, stored as-is. Alvo will not validate or index inside it.',
  enum: 'One of a fixed set you name. Alvo refuses any other value.',
  ref: 'Points at a record of another entity. This field is the relationship.',
};

const ON_DELETE_WORDS = { restrict: 'Refuse the delete', setNull: 'Clear this field', cascade: 'Delete this record too' };

/* --- Relationships -------------------------------------------------------- */

function relationshipsTab(e) {
  const out = e.fields.filter((f) => f.type === 'ref');
  const incoming = [];
  entities().forEach((o) => o.fields.filter((f) => f.type === 'ref' && f.entity === e.name)
    .forEach((f) => incoming.push({ from: o.name, field: f.name, onDelete: f.onDelete })));

  const rollups = entities().flatMap((o) => o.fields.filter((f) => f.rollup?.from === e.name).map((f) => `${o.name}.${f.name}`));

  const outHtml = out.length ? out.map((f) => `<div class="a-rel">
      <div class="a-rel__side"><span class="a-rel__entity">${e.name}</span><span class="a-rel__field">${f.name}</span></div>
      <div class="a-rel__link"><span>many to one</span><span class="a-rel__wire"></span><span class="a-badge">on delete ${f.onDelete ?? 'restrict (default)'}</span></div>
      <div class="a-rel__side"><span class="a-rel__entity">${f.entity}</span><span class="a-rel__field">id</span></div>
    </div>`).join('') : `<div class="a-empty"><span class="a-empty__title">Nothing points out of ${e.name}</span>
      <span class="a-empty__body">Add a field of type <code class="a-mono">ref</code> to connect this entity to another one. That field is the relationship — Alvo has no separate object for it.</span>
      <button class="a-btn a-btn--primary" data-act="field" data-field="__new">Add a ref field</button></div>`;

  const inHtml = incoming.length ? incoming.map((r) => `<div class="a-rel">
      <div class="a-rel__side"><span class="a-rel__entity">${r.from}</span><span class="a-rel__field">${r.field}</span></div>
      <div class="a-rel__link"><span>points here</span><span class="a-rel__wire"></span><span class="a-badge">on delete ${r.onDelete ?? 'restrict (default)'}</span></div>
      <div class="a-rel__side"><span class="a-rel__entity">${e.name}</span><span class="a-rel__field">id</span></div>
    </div>`).join('') + `<div class="p-note" style="margin:var(--space-4)"><span class="p-note__tag">why it matters</span>
      <span>Because something points here, a record's detail page can list what refers to it, and a field on ${e.name} can aggregate over them with a <code class="a-mono">rollup</code>. ${rollups.length ? `<code class="a-mono">${rollups.join('</code>, <code class="a-mono">')}</code> already ${rollups.length > 1 ? 'do' : 'does'}.` : 'None does yet.'}</span></div>`
    : '<div class="a-empty"><span class="a-empty__body">No other entity points at this one yet.</span></div>';

  return `<div class="a-section" style="border-top:none"><span class="a-section-title">Out of ${e.name}</span>
      <span class="a-section-sub">One row per <code class="a-mono">ref</code> field.</span></div>${outHtml}
    <div class="a-section"><span class="a-section-title">Into ${e.name}</span>
      <span class="a-section-sub"><code class="a-mono">restrict</code> means a record here cannot be deleted while one of these points at it.</span></div>${inHtml}`;
}

/* --- On write (hooks) ----------------------------------------------------- */

const HOOK_POINTS = [
  ['beforeCreate', 'in the same transaction'],
  ['beforeUpdate', 'in the same transaction'],
  ['beforeDelete', 'in the same transaction'],
  ['afterCreate', 'after commit, from the outbox'],
  ['afterUpdate', 'after commit, from the outbox'],
  ['afterDelete', 'after commit, from the outbox'],
];

/** The action types `$defs/action` declares. Three are refused by this build, by name. */
const ACTION_TYPES = [
  { type: 'reject', honoured: true, what: 'Refuse the write with a message' },
  { type: 'mutate', honoured: true, what: 'Set a field on the row being written' },
  { type: 'email', honoured: true, what: 'Render a template and send it' },
  { type: 'webhook', honoured: true, what: 'Post to a declared endpoint' },
  { type: 'function', honoured: false, what: 'Invoke a declared function' },
  { type: 'http.call', honoured: false, what: 'Call a URL directly' },
  { type: 'entity.update', honoured: false, what: 'Write to another entity' },
];

function hooksTab(e) {
  const hooks = e.hooks ?? {};
  const total = hookCount(e);

  const row = (point, when, h, index) => `<div class="a-hook">
      <span class="a-hook__point">
        <code class="a-mono" style="color:var(--text);font-size:var(--text-sm)">${point}</code>
        <span class="a-hook__when">${when}</span></span>
      <span style="min-width:0;display:flex;flex-direction:column;gap:var(--space-2)">
        ${h.when ? `<span class="p-muted">only when <code class="a-mono" style="color:var(--text)">${esc(h.when)}</code></span>` : '<span class="p-muted">on every write</span>'}
        <span class="a-row">
          <span class="a-badge${h.type === 'reject' ? ' a-badge--danger' : h.type === 'mutate' ? '' : ' a-badge--accent'}">${esc(h.type)}</span>
          <span style="font-size:var(--text-sm)">${esc(hookSummary(h))}</span></span></span>
      <button class="a-btn a-btn--sm a-btn--ghost" data-act="removehook" data-point="${point}" data-i="${index}">Remove</button></div>`;

  const section = (kind) => HOOK_POINTS.filter(([p]) => p.startsWith(kind))
    .flatMap(([point, when]) => (hooks[point] ?? []).map((h, i) => row(point, when, h, i))).join('');

  const before = section('before');
  const after = section('after');

  return `<div class="a-toolbar">
      <span class="p-muted">A hook is a condition and an action, at one point of one write. The two kinds are never one list.</span>
      <button class="a-btn a-btn--sm a-btn--primary" style="margin-left:auto" data-act="overlay" data-kind="new-hook" data-id="${e.name}">${icon('plus')} Add a hook</button></div>
    ${total === 0 ? `<div class="a-empty"><span class="a-empty__title">Nothing happens on a write to ${e.name}</span>
      <span class="a-empty__body">A hook can refuse a write before it commits, fill a field in, or — once committed — send an email or call a webhook. Both kinds run today; three of the seven action types do not, and this editor says which.</span>
      <button class="a-btn a-btn--primary" data-act="overlay" data-kind="new-hook" data-id="${e.name}">${icon('plus')} Add a hook</button></div>` : `
    <div class="a-section"><span class="a-section-title">Before the write commits</span>
      <span class="a-section-sub">In the same transaction. May refuse the write or change the values, and reaches no network.</span></div>
    ${before || '<div class="a-empty"><span class="a-empty__body">No before-hook on this entity.</span></div>'}
    <div class="a-section"><span class="a-section-title">After the write commits</span>
      <span class="a-section-sub">From the outbox, with retries. A failure here never rolls back the write that caused it.</span></div>
    ${after || '<div class="a-empty"><span class="a-empty__body">No after-hook on this entity.</span></div>'}`}
    <div class="a-row" style="padding:var(--space-4);color:var(--faint);font-size:var(--text-xs)">
      <span>A before-hook reads <code class="a-mono">new.</code> and <code class="a-mono">old.</code>; a rule on the Rules tab reads the bare field name. Two vocabularies, because a rule sees one stored row and a hook sees the write that is changing it. On a create there is no <code class="a-mono">old.</code> at all.</span></div>`;
}

const hookSummary = (h) => ({
  reject: h.message ?? '',
  mutate: `${h.field ?? ''} ← ${h.value ?? ''}`,
  email: `${h.template ?? ''} → ${h.to ?? ''}`,
  webhook: h.endpoint ?? '',
}[h.type] ?? h.type);

/* --- Indexes -------------------------------------------------------------- */

function indexesTab(e) {
  const single = e.fields.filter((f) => f.index).map((f) => f.name);

  const body = e.indexes.length || single.length
    ? `${single.length ? `<div class="a-row" style="padding:var(--space-4);border-bottom:1px solid var(--border)">
        <span class="p-muted">From a field's own <code class="a-mono">index: true</code>: <code class="a-mono">${single.join('</code>, <code class="a-mono">')}</code></span></div>` : ''}
      ${e.indexes.map((ix, i) => `<div class="a-row" style="padding:var(--space-4);border-bottom:1px solid var(--border)">
        <code class="a-mono" style="font-size:var(--text-sm);color:var(--text)">${ix.fields.join(', ')}</code>
        ${ix.unique ? '<span class="a-badge a-badge--warn">unique</span>' : ''}
        <span class="p-muted">covers a filter on ${ix.fields[0]}${ix.fields.length > 1 ? `, then ${ix.fields.slice(1).join(', ')}` : ''}</span>
        <button class="a-btn a-btn--sm a-btn--ghost" style="margin-left:auto" data-act="removeindex" data-i="${i}">Remove</button></div>`).join('')}`
    : `<div class="a-empty"><span class="a-empty__title">No index beyond the primary key</span>
        <span class="a-empty__body">Add one when a column shows up in a filter or a sort you run often. ${e.name} is at ${num(ROW_COUNTS[e.name] ?? 0)} records.</span></div>`;

  return `<div class="a-toolbar">
      <span class="p-muted">A composite index is ordered — the first column is the one a filter must name.</span>
      <button class="a-btn a-btn--sm a-btn--primary" style="margin-left:auto" data-act="overlay" data-kind="new-index" data-id="${e.name}">${icon('plus')} Add index</button></div>
    ${body}
    <div class="a-row" style="padding:var(--space-4);color:var(--faint);font-size:var(--text-xs)">
      <span><code class="a-mono">index: true</code> on a field and a one-column entry in <code class="a-mono">indexes</code> are the same index. Use the field toggle for one column and this tab when the order of two or more matters.</span></div>`;
}

/* --- The API tab ---------------------------------------------------------- */

function apiTab(e) {
  const sample = { id: '9f1c4a20-7d38-4a5e-9c11-2b6e0d4f8a4e' };
  const row = ROWS[e.name]?.[0];
  e.fields.filter((f) => f.hidden !== true).slice(0, 7).forEach((f) => {
    sample[f.name] = row?.[f.name] ?? sampleValue(f);
  });
  if (e.audit) Object.assign(sample, { created_at: '2026-09-18T16:02:11Z', updated_at: '2026-09-21T09:14:02Z' });

  const sortable = e.fields.find((f) => f.required && ['integer', 'decimal', 'string', 'enum'].includes(f.type));
  const filterable = e.fields.find((f) => f.type === 'boolean' && f.hidden !== true)
    ?? e.fields.find((f) => f.type === 'enum' && f.hidden !== true);

  const routes = [
    ['GET', `/api/${e.name}`, 'List, filtered and sorted. Keyset paging.', 'list'],
    ['GET', `/api/${e.name}/{id}`, 'One record.', 'get'],
    ['POST', `/api/${e.name}`, 'Create. Idempotent with an Idempotency-Key header.', 'create'],
    ['PATCH', `/api/${e.name}/{id}`, 'Change some fields.', 'update'],
    ['PUT', `/api/${e.name}/{id}`, 'Create or replace at an id you choose.', 'update'],
    ['DELETE', `/api/${e.name}/{id}`, 'Remove.', 'delete'],
    ['POST', `/api/${e.name}/query`, 'The same read as GET, for filters too long for a URL.', 'list'],
    ['POST', `/api/${e.name}/batch`, 'Create, update and delete in one transaction, by id.', 'update'],
  ];

  return `<div class="a-toolbar"><span class="p-muted">Generated from this entity. Change a field and these change with it — there is no second place to update.</span>
      <span style="margin-left:auto" class="p-hstack">
        ${inert('Open reference', 'The generated OpenAPI document is served by the running instance; this drawing has none to link to.')}
        ${inert('Download OpenAPI', 'Same reason: the document is generated at runtime from the applied schema.')}</span></div>
    ${routes.map(([m, path, what, op]) => {
      const model = ruleModel(e, op);
      const cel = celOf(model, e);
      return `<div class="a-row" style="align-items:flex-start;padding:var(--space-3) var(--space-4);border-bottom:1px solid var(--border)">
      <span class="a-badge${m === 'GET' ? '' : m === 'DELETE' ? ' a-badge--danger' : ' a-badge--accent'}" style="flex:none;width:62px;justify-content:center">${m}</span>
      <span style="flex:1;min-width:0">
        <code class="a-mono" style="color:var(--text);font-size:var(--text-sm)">${path}</code>
        <span class="a-switcher-meta">${what}</span></span>
      <span style="flex:none;max-width:40%;text-align:right">
        ${cel ? sentenceOf(model, e) : '<span class="a-badge a-badge--danger">no rule — refused for everyone</span>'}</span>
    </div>`;
    }).join('')}

    <div style="padding:var(--space-4);display:grid;grid-template-columns:repeat(auto-fit,minmax(300px,1fr));gap:var(--space-4)">
      <div class="a-stack" style="gap:var(--space-2)">
        <span class="a-label">A filtered page, newest first by a required column</span>
        <pre class="a-code">curl -H "X-Alvo-Api-Key: $ALVO_KEY" \\
  "$HOST/api/${e.name}?${filterable ? `${filterable.name}=eq.${filterable.type === 'boolean' ? 'true' : filterable.values[0]}&` : ''}\\
order=${sortable ? sortable.name : 'id'}.desc&limit=20"</pre>
        <span class="p-muted">${sortable
          ? `Sorted by <code class="a-mono">${sortable.name}</code> because it is <strong>required</strong>. Sorting by a nullable column is legal and slower — the keyset predicate has to order the nulls too.`
          : 'No required sortable column on this entity, so this sorts by <code class="a-mono">id</code>.'}</span>
      </div>
      <div class="a-stack" style="gap:var(--space-2)">
        <span class="a-label">What comes back</span>
        <pre class="a-code">${highlight(JSON.stringify(sample, null, 2))}</pre>
        <span class="p-muted">${e.fields.filter((f) => f.hidden === true).length} hidden field(s) are in no response and in no published read schema. A <em>required</em> hidden one is named in the write schemas — that is the one exception.</span>
      </div>
    </div>`;
}

function sampleValue(f) {
  switch (f.type) {
    case 'enum': return f.values?.[0] ?? 'value';
    case 'integer': return 1;
    case 'decimal': return 480.0;
    case 'boolean': return true;
    case 'datetime': return '2026-09-22T08:30:00Z';
    case 'date': return '2026-09-22';
    case 'uuid': case 'ref': return '9f1c4a20-7d38-4a5e-9c11-2b6e0d4f8a4e';
    case 'json': return {};
    default: return 'value';
  }
}

/* ==========================================================================
   What a rule can actually say

   Checked against docs/architecture/cel.md — the Rule column of the profile table. A rule MAY:

     - test role membership            'x' in @user.roles          and negate it
     - compare a field of this row     status != 'cancelled'
       to a literal, to @user.id, to @tenant.id, or to another field
     - use a boolean field bare        is_emergency        and negate it
     - test presence                   has(scheduled_for)  !has(completed_on)
     - combine with && || ! and parentheses, freely

   It MAY NOT call a function (has() and changed() are the only names before a paren, and
   changed() is a Condition-profile construct, not a Rule one), may not do arithmetic, and @user
   is a closed set of id and roles — there is no @user.email, so an attribute gate on a mail
   domain is not expressible in any block. `== null` is REJECTED outright: use has().

   So a rule is: alternatives joined by ||, each one a way IN that may be narrowed by tests that
   must all hold, and the tests belong to the BRANCH rather than to the column.
   ========================================================================== */

const OPS = [
  ['list', 'Read many', (n) => `GET /api/${n}`],
  ['get', 'Read one', (n) => `GET /api/${n}/{id}`],
  ['create', 'Create', (n) => `POST /api/${n}`],
  ['update', 'Change', (n) => `PATCH /api/${n}/{id}`],
  ['delete', 'Delete', (n) => `DELETE /api/${n}/{id}`],
];

const OPERATORS = {
  enum: [['==', 'is'], ['!=', 'is not']],
  string: [['==', 'is'], ['!=', 'is not'], ['has', 'is set'], ['!has', 'is empty']],
  integer: [['==', 'is'], ['!=', 'is not'], ['<=', 'is at most'], ['>=', 'is at least']],
  decimal: [['<=', 'is at most'], ['>=', 'is at least']],
  boolean: [['true', 'is true'], ['false', 'is false']],
  date: [['has', 'is set'], ['!has', 'is empty'], ['<=', 'is on or before'], ['>=', 'is on or after']],
  datetime: [['has', 'is set'], ['!has', 'is empty'], ['<=', 'is on or before'], ['>=', 'is on or after']],
  uuid: [['==', 'is'], ['!=', 'is not'], ['has', 'is set'], ['!has', 'is empty']],
  ref: [['==', 'is'], ['!=', 'is not'], ['has', 'is set'], ['!has', 'is empty']],
};

const NO_VALUE = ['has', '!has', 'true', 'false'];

const testable = (e) => e.fields.filter((f) => f.hidden !== true && OPERATORS[f.type] && !f.computed && !f.rollup);

function newCond(e, f) {
  const op = (OPERATORS[f.type] || OPERATORS.string)[0][0];
  if (NO_VALUE.includes(op)) return { field: f.name, op, value: '' };
  const opts = valueOptions(e, f);
  return { field: f.name, op, value: opts.length ? opts[0][0] : '1' };
}

function valueOptions(e, f) {
  const out = [];
  if (f.values) out.push(...f.values.map((v) => [v, v]));
  if (f.type === 'boolean') out.push(['true', 'true'], ['false', 'false']);
  if (f.type === 'uuid' || f.type === 'ref') out.push(['@user.id', 'the caller'], ['@tenant.id', "the caller's tenant"]);
  e.fields.filter((x) => x.type === f.type && x.name !== f.name && x.hidden !== true)
    .forEach((x) => out.push([`:${x.name}`, `the ${x.name} of this record`]));
  return out;
}

const isFieldRef = (v) => typeof v === 'string' && v.startsWith(':');
const isContextRef = (v) => v === '@user.id' || v === '@tenant.id';

function litOf(f, v) {
  if (isFieldRef(v)) return v.slice(1);
  if (isContextRef(v)) return v;
  if (f && (f.type === 'boolean' || f.type === 'integer' || f.type === 'decimal')) return String(v);
  return `'${v}'`;
}

function condCel(e, c) {
  const f = e.fields.find((x) => x.name === c.field);
  if (c.op === 'has') return `has(${c.field})`;
  if (c.op === '!has') return `!has(${c.field})`;
  if (c.op === 'true') return c.field;
  if (c.op === 'false') return `!${c.field}`;
  return `${c.field} ${c.op} ${litOf(f, c.value)}`;
}

const whoCel = (b) => (b.kind === 'role' ? `'${b.role}' in @user.roles` : `${b.field} == @user.id`);

function celOf(m, e) {
  if (m.raw !== null) return m.raw;
  const bs = m.branches || [];
  if (!bs.length) return '';
  const key = (b) => (b.conds || []).map((c) => condCel(e, c)).join('&&');
  const shared = bs.length > 1 && bs.every((b) => key(b) === key(bs[0])) && (bs[0].conds || []).length;
  if (shared) {
    const left = bs.length > 1 ? `(${bs.map(whoCel).join(' || ')})` : whoCel(bs[0]);
    return [left, ...bs[0].conds.map((c) => condCel(e, c))].join(' && ');
  }
  return bs.map((b) => {
    const parts = [whoCel(b), ...(b.conds || []).map((c) => condCel(e, c))];
    return parts.length > 1 && bs.length > 1 ? `(${parts.join(' && ')})` : parts.join(' && ');
  }).join(' || ');
}

const roleWord = (r) => (r === 'authenticated'
  ? 'Anyone signed in'
  : { admin: 'Admins', dispatcher: 'Dispatchers', technician: 'Technicians', anon: 'Anyone at all' }[r]
    || `Anyone holding ${r}`);

const opWord = (f, op) => ((OPERATORS[f ? f.type : 'string'] || OPERATORS.string).find(([o]) => o === op) || [op, op])[1];

function valueWord(e, f, c) {
  if (NO_VALUE.includes(c.op)) return '';
  const opt = valueOptions(e, f).find(([v]) => String(v) === String(c.value));
  return opt ? opt[1] : c.value;
}

function condWords(e, c) {
  const f = e.fields.find((x) => x.name === c.field);
  const v = valueWord(e, f, c);
  return `<code class="a-mono">${c.field}</code> ${opWord(f, c.op)}${v ? ` <code class="a-mono">${esc(v)}</code>` : ''}`;
}

function sentenceOf(m, e) {
  if (m.raw !== null) return 'A hand-written expression — the controls below cannot represent it.';
  const bs = m.branches || [];
  if (!bs.length) return '<strong>Nobody.</strong> Refused for every caller, an admin included.';
  if (bs.some((b) => b.kind === 'role' && b.role === 'anon')) {
    const anon = bs.find((b) => b.kind === 'role' && b.role === 'anon');
    const w = (anon.conds || []).map((c) => condWords(e, c));
    return `<strong>Anyone</strong>, signed in or not${w.length ? `, while ${w.join(' and ')}` : ''}`;
  }
  const parts = bs.map((b, i) => {
    let who = b.kind === 'role' ? roleWord(b.role) : `the user in <code class="a-mono">${b.field}</code>`;
    if (i && b.kind === 'role') who = who.charAt(0).toLowerCase() + who.slice(1);
    const w = (b.conds || []).map((c) => condWords(e, c));
    return w.length ? `${who} while ${w.join(' and ')}` : who;
  });
  /* Past four branches the sentence stops being a sentence. */
  if (parts.length > 4) {
    const withConds = bs.filter((b) => (b.conds || []).length).length;
    return `<strong>${parts.length} ways in</strong> — ${bs.filter((b) => b.kind === 'role').map((b) => b.role).join(', ')}${bs.some((b) => b.kind === 'owner') ? ', and the user the record names' : ''}${withConds ? `; ${withConds} of them narrowed` : ''}. Open the editor to read them.`;
  }
  if (parts.length === 1) return parts[0];
  const sep = bs.some((b) => (b.conds || []).length) ? ';' : ',';
  if (parts.length === 2) return `${parts[0]}${sep} or ${parts[1]}`;
  return `${parts.slice(0, -1).join(sep + ' ')}${sep} or ${parts[parts.length - 1]}`;
}

/* --- Reading an expression back ------------------------------------------ */

function splitTop(s, sep) {
  const out = [];
  let depth = 0, cur = '', q = false;
  for (let i = 0; i < s.length; i++) {
    const ch = s[i];
    if (ch === "'") q = !q;
    if (!q) {
      if (ch === '(') depth++;
      if (ch === ')') depth--;
      if (!depth && s.startsWith(sep, i)) { out.push(cur); cur = ''; i += sep.length - 1; continue; }
    }
    cur += ch;
  }
  out.push(cur);
  return out.map((x) => x.trim());
}

const unwrap = (s) => {
  let t = s.trim();
  while (t.startsWith('(') && t.endsWith(')')) {
    const inner = t.slice(1, -1);
    let d = 0, ok = true;
    for (const ch of inner) { if (ch === '(') d++; if (ch === ')') d--; if (d < 0) ok = false; }
    if (!ok || d !== 0) break;
    t = inner.trim();
  }
  return t;
};

function parseWho(part) {
  /* `$defs/identifier` allows digits, so `tier2` is a legal role name. */
  const role = part.match(/^'([a-z][a-z0-9_-]*)' in @user\.roles$/);
  if (role) return { kind: 'role', role: role[1], conds: [] };
  const own = part.match(/^([a-z][a-z0-9_]*) == @user\.id$/);
  if (own) return { kind: 'owner', field: own[1], conds: [] };
  return null;
}

function parseCond(part) {
  let m = part.match(/^has\(([a-z][a-z0-9_]*)\)$/);
  if (m) return { field: m[1], op: 'has', value: '' };
  m = part.match(/^!has\(([a-z][a-z0-9_]*)\)$/);
  if (m) return { field: m[1], op: '!has', value: '' };
  m = part.match(/^([a-z][a-z0-9_]*) (==|!=|<=|>=|<|>) (.+)$/);
  if (m) {
    let v = m[3].trim();
    if (v === 'null') return null;   // `== null` is rejected by the compiler; never parse it as a value
    if (v.startsWith("'") && v.endsWith("'")) v = v.slice(1, -1);
    else if (/^[a-z][a-z0-9_]*$/.test(v) && !['true', 'false'].includes(v)) v = ':' + v;
    return { field: m[1], op: m[2], value: v };
  }
  m = part.match(/^!([a-z][a-z0-9_]*)$/);
  if (m) return { field: m[1], op: 'false', value: '' };
  m = part.match(/^([a-z][a-z0-9_]*)$/);
  if (m) return { field: m[1], op: 'true', value: '' };
  return null;
}

function parseCel(cel) {
  const empty = { branches: [], raw: null };
  if (!cel || !cel.trim()) return empty;
  const top = splitTop(cel, '&&');

  if (top.length > 1) {
    const heads = splitTop(unwrap(top[0]), '||');
    const whos = heads.map((h) => parseWho(unwrap(h)));
    if (whos.every(Boolean)) {
      const conds = top.slice(1).map((t) => parseCond(unwrap(t)));
      if (conds.every(Boolean)) return { branches: whos.map((w) => ({ ...w, conds: [...conds] })), raw: null };
    }
  }

  const branches = [];
  for (const raw of splitTop(cel, '||')) {
    const parts = splitTop(unwrap(raw), '&&');
    const who = parseWho(unwrap(parts[0]));
    if (!who) return { branches: [], raw: cel };
    const conds = parts.slice(1).map((p) => parseCond(unwrap(p)));
    if (!conds.every(Boolean)) return { branches: [], raw: cel };
    branches.push({ ...who, conds });
  }
  return { branches, raw: null };
}

/** The model for one operation, parsed from the WORKING copy every time it is asked for. */
function ruleModel(e, op) {
  const key = `${e.name}:${op}`;
  const cel = e.rules?.[op] ?? '';
  if (state.rawOps?.has(key)) return { branches: [], raw: cel };
  return parseCel(cel);
}

/** Writes a model back as CEL. The round trip is exact for every shape the builder can write. */
function writeRule(e, op, model) {
  editors.setRule(e.name, op, celOf(model, e));
}

const branchFor = (m, who) => (m.branches || []).find((b) => (who.kind === 'role' ? b.kind === 'role' && b.role === who.role : b.kind === 'owner' && b.field === who.field));

function rolesFor(e) {
  const used = new Set();
  OPS.forEach(([op]) => (ruleModel(e, op).branches || []).forEach((b) => b.kind === 'role' && used.add(b.role)));
  const builtIn = BUILTIN_ROLES.map(([r]) => r);
  const declared = declaredRoles().filter((r) => !builtIn.includes(r));
  const stray = [...used].filter((r) => !builtIn.includes(r) && !declared.includes(r));
  return [...builtIn, ...declared, ...stray];
}

/* ==========================================================================
   The rules screen: a matrix, the sentences, and a simulator that renders a
   verdict rather than scoring a row.
   ========================================================================== */

function permissionMatrix(e, roles) {
  const uuidFields = e.fields.filter((f) => f.type === 'uuid');
  const WORDS = Object.fromEntries(BUILTIN_ROLES);
  const builtIn = BUILTIN_ROLES.map(([r]) => r);

  const cell = ({ on, via, when, act, data, fixed, pub }) => `<button
      class="a-cell${on ? ' a-cell--on' : ''}${via ? ' a-cell--via' : ''}${fixed ? ' a-cell--fixed' : ''}${pub ? ' a-cell--public' : ''}"
      ${fixed ? 'disabled aria-disabled="true" title="This rule is hand-written; the matrix cannot change it"' : `data-act="${act}" ${data}`} type="button" aria-pressed="${!!on}"
      ${via ? 'title="Not granted by this role — the record grants it, through the field named below"' : ''}>
      <span class="a-cell__mark">${on ? icon('check') : via ? 'own' : ''}</span>
      ${when ? `<span class="a-cell__when">${when}</span>` : ''}</button>`;

  const roleRow = (r) => `<tr data-role-row="${r}">
      <td><span class="a-matrix__who">
        <span class="a-matrix__name">${r}</span>
        <span class="a-matrix__sub">${WORDS[r] ? esc(WORDS[r]) : `'${r}' in @user.roles`}</span></span></td>
      ${OPS.map(([op]) => {
        const m = ruleModel(e, op);
        const b = branchFor(m, { kind: 'role', role: r });
        const owner = (m.branches || []).find((x) => x.kind === 'owner');
        return `<td>${cell({
          on: !!b,
          via: !b && !!owner && r !== 'anon',
          when: b && (b.conds || []).length ? `${b.conds.length} condition${b.conds.length > 1 ? 's' : ''}` : (!b && owner && r !== 'anon' ? `via ${owner.field}` : ''),
          act: 'rulerole',
          data: `data-op="${op}" data-entity="${e.name}" data-role="${r}"`,
          fixed: m.raw !== null,
          pub: !!b && r === 'anon',
        })}</td>`;
      }).join('')}
    </tr>`;

  const ownerRow = (f) => `<tr>
      <td><span class="a-matrix__who">
        <span class="a-matrix__name">The user in ${f.name}</span>
        <span class="a-matrix__sub">${f.name} == @user.id · any signed-in caller</span></span></td>
      ${OPS.map(([op]) => {
        const m = ruleModel(e, op);
        const b = branchFor(m, { kind: 'owner', field: f.name });
        return `<td>${cell({
          on: !!b,
          when: b && (b.conds || []).length ? `${b.conds.length} condition${b.conds.length > 1 ? 's' : ''}` : '',
          act: 'ruleowner',
          data: `data-op="${op}" data-entity="${e.name}" data-field="${f.name}"`,
          fixed: m.raw !== null,
        })}</td>`;
      }).join('')}
    </tr>`;

  const declared = roles.filter((r) => !builtIn.includes(r));
  const isPublic = OPS.some(([op]) => branchFor(ruleModel(e, op), { kind: 'role', role: 'anon' }));
  const hasOwner = OPS.some(([op]) => (ruleModel(e, op).branches || []).some((b) => b.kind === 'owner'));

  return `<div class="a-matrix-wrap"><table class="a-matrix">
    <colgroup><col>${OPS.map(() => '<col class="a-matrix__opcol">').join('')}</colgroup>
    <thead><tr><th>Who</th>${OPS.map(([, verb]) => `<th>${verb}</th>`).join('')}</tr></thead>
    <tbody>
      <tr class="a-matrix__group"><td colspan="6">Built in · always present, never declared</td></tr>
      ${roles.filter((r) => builtIn.includes(r)).map(roleRow).join('')}
      ${isPublic ? `<tr><td colspan="6" style="padding:0"><div class="a-public-warn">⚠
        <span>Anyone who can reach the URL may do that, signed in or not. Right for a public catalogue, wrong for everything else.</span></div></td></tr>` : ''}
      <tr class="a-matrix__group"><td colspan="6">Declared by this project · <a style="color:var(--accent)" href="#/access">manage in Access</a></td></tr>
      ${declared.length ? declared.map(roleRow).join('') : '<tr><td colspan="6" class="p-muted" style="padding:var(--space-4)">This project declares no roles of its own yet.</td></tr>'}
      ${uuidFields.length ? `<tr class="a-matrix__group"><td colspan="6">Through the record itself</td></tr>${uuidFields.map(ownerRow).join('')}` : ''}
    </tbody>
  </table></div>
  ${hasOwner ? `<div class="a-row" style="padding:var(--space-3) var(--space-5);color:var(--faint);font-size:var(--text-xs)">
    <span><strong>own</strong> in a role's cell is not a grant from that role. It marks that the record itself admits the caller through the field named beside it — so a caller with only that role reaches the rows the field names, and no others.</span></div>` : ''}`;
}

/* The compact read-only view, inside the entity editor's Rules tab. */
function rulesList(e) {
  return `<div class="a-toolbar">
      <span class="p-muted">Who may do each thing to these records, and when. An operation with no rule is refused for everyone — there is no implicit allow.</span>
      <a class="a-btn a-btn--sm" style="margin-left:auto" href="#/rules/${e.name}">Edit, and read the predicate</a></div>
    ${OPS.every(([op]) => !celOf(ruleModel(e, op), e)) ? `<div class="a-empty">
      <span class="a-empty__title">No rule at all — refused for everyone</span>
      <span class="a-empty__body">Default-deny: with no rule, every operation on ${e.name} answers 403 for every caller, an administrator included. Nothing is implicitly allowed.</span>
      <a class="a-btn a-btn--primary" href="#/rules/${e.name}">Write the first rule</a></div>` : ''}
    ${OPS.map(([op, verb, api]) => {
      const m = ruleModel(e, op);
      return `<div class="a-perm">
        <span class="a-perm__op"><span class="a-perm__verb">${verb}</span><span class="a-perm__api">${esc(api(e.name))}</span></span>
        <span><span class="a-perm__who">${sentenceOf(m, e)}</span>
          <span class="a-perm__cel">${esc(celOf(m, e)) || '// no rule'}</span></span>
        <span>${celOf(m, e) ? '' : '<span class="a-badge a-badge--danger">refused</span>'}</span>
      </div>`;
    }).join('')}`;
}

function screenRules(name) {
  const e = entityView(name) || entities()[0];
  if (!e) return screenSchemaList();

  const ruleEdits = changes().filter((c) => c.kind === 'rules' && c.pointer.startsWith(`/entities/${e.name}/`));

  const rows = OPS.map(([op, verb, api]) => {
    const m = ruleModel(e, op);
    const open = state.ruleOpen === op;
    return `<div class="a-perm${open ? ' a-perm--open' : ''}" id="rule-${op}">
      <span class="a-perm__op"><span class="a-perm__verb">${verb}</span><span class="a-perm__api">${esc(api(e.name))}</span></span>
      <span><span class="a-perm__who">${sentenceOf(m, e)}</span>
        <span class="a-perm__cel">${esc(celOf(m, e)) || '// no rule — refused for everyone'}</span></span>
      <button class="a-btn a-btn--sm${open ? ' a-btn--primary' : ''}" data-act="ruleopen" data-op="${op}">${open ? 'Done' : 'Change'}</button>
      ${open ? ruleEditor(e, op, m) : ''}
    </div>`;
  }).join('');

  return `${header([{ label: 'Rules' }], count() ? `<button class="a-btn a-btn--primary" data-act="go" data-route="#/schema/preview">Preview (${count()})</button>` : '')}
  <div class="a-content"><div class="a-stack">
    ${entityBar(e.name, '#/rules', '<span class="p-muted">who may read and write each one</span>')}

    <div><h1 class="a-page-title">Who can do what to <span style="font-family:var(--font-mono)">${e.name}</span></h1>
      <p class="p-muted p-tight" style="max-width:74ch">A rule is a set of ways in, joined by <em>or</em>: a role, or the user the record names. Each way in can be narrowed by tests on the record that must all hold. It is checked inside the same transaction as the query, and it can read the caller's id, their roles, their tenant, and this record's own fields — nothing else.</p></div>

    <div class="a-panel a-matrix-panel">
      <div class="a-section"><span class="a-section-title">Permissions</span>
        <span class="a-section-sub">Tick to grant. Use Change below to narrow a grant.</span></div>
      ${permissionMatrix(e, rolesFor(e))}
      <div class="a-row" style="padding:var(--space-3) var(--space-5);color:var(--faint);font-size:var(--text-xs)">
        <span>The five columns are all there are — Alvo generates exactly these operations from the entity. A role missing here is one the descriptor does not declare.</span>
      </div>
    </div>

    <div class="a-split a-split--wide">
      <div class="a-stack">
        <div class="a-panel">
          <div class="a-section"><span class="a-section-title">What each one says</span>
            <span class="a-section-sub">The same five as sentences, and the CEL they produce. The editor opens inside the row.</span></div>
          ${rows}
        </div>
        ${pendingBar()}
      </div>

      <div class="a-split__aside">${simulator(e, ruleEdits.length)}</div>
    </div>
  </div></div>`;
}

/* --- The simulator: a verdict, never a row score -------------------------- */

function simulator(e, draftRules) {
  const who = userById(state.simulate.user);
  const operation = state.simulate.operation;
  const caller = callerFor(who.id);
  /* It answers against the WORKING copy when one exists, and says so. */
  const doc = draftRules ? wc.working : wc.applied;
  const v = verdict(e.name, operation, caller, doc);
  const outcome = outcomeOfFailingUsing(operation);

  const callers = [
    ...USERS.map((u) => ({ id: u.id, label: u.email.split('@')[0], roles: mintedRoles(u.id) })),
    { id: null, label: 'anon', roles: ['anon'] },
  ];

  const line = (label, value, hint) => `<div class="a-field"><span class="a-label">${label}${hint ? `<span class="a-label__hint">${hint}</span>` : ''}</span>
    ${value}</div>`;

  return `<div class="a-card" style="gap:var(--space-4)" data-simulator>
    <div><span class="a-section-title">Simulate a policy</span>
      <p class="p-muted p-tight">This is <code class="a-mono">POST ${mgmt('/projects/{project}/policy/simulate')}</code>: an entity, an operation and a caller in, the engine's own verdict out.
      ${draftRules ? '<strong>Answering against your unapplied draft</strong> — callers still get revision ' + wc.revision + '.' : ''}</p></div>

    ${line('Signed in as', `<div class="p-hstack">${callers.map((c) => `<button class="a-preset${(state.simulate.user ?? null) === c.id ? ' a-preset--on' : ''}" data-act="sim" data-k="user" data-v="${c.id ?? ''}">${esc(c.label)}</button>`).join('')}</div>`,
      caller.roles.length ? `roles: <code class="a-mono">${caller.roles.join(', ')}</code> · tenant: <code class="a-mono">${caller.tenant ? esc(shortTenant(caller.tenant)) : 'none'}</code>` : 'the anonymous caller holds only <code class="a-mono">anon</code> and carries no tenant')}

    ${line('Operation', `<div class="p-hstack">${OPERATIONS.map((op) => `<button class="a-preset${operation === op ? ' a-preset--on' : ''}" data-act="sim" data-k="operation" data-v="${op}">${op}</button>`).join('')}</div>`)}

    <div class="a-verdict a-verdict--${v.allowed ? 'allow' : 'deny'}" data-verdict="${v.allowed ? 'resolved' : v.cause}">
      <span class="a-verdict__head">${v.allowed ? 'A policy resolved' : '403 — refused outright'}</span>
      ${v.allowed
        ? `<span class="a-verdict__why">${esc(ALLOWED_MEANS)}</span>`
        : `<span class="a-verdict__why"><strong>${esc(CAUSES[v.cause].title)}.</strong> ${esc(CAUSES[v.cause].detail)}</span>
           <div class="a-readout"><span class="a-readout__tag">engine</span><span>${esc(v.denyReason)}</span></div>`}
    </div>

    ${v.allowed ? `
      ${v.using ? `<div class="a-field"><span class="a-label">USING — the read predicate<span class="a-label__hint">Every row this caller can reach must satisfy it.</span></span>
        <div class="a-readout"><span class="a-readout__tag">cel</span><span>${esc(v.using)}</span></div></div>` : ''}
      ${v.withCheck ? `<div class="a-field"><span class="a-label">WITH CHECK — the write predicate<span class="a-label__hint">Every row this caller writes must satisfy it after the write.</span></span>
        <div class="a-readout"><span class="a-readout__tag">cel</span><span>${esc(v.withCheck)}</span></div></div>` : ''}
      ${v.tenantScope ? `<div class="a-field"><span class="a-label">Tenant scope<span class="a-label__hint">Synthesised by the framework because this entity is <code class="a-mono">tenancy: scoped</code>. It is ANDed with everything above.</span></span>
        <div class="a-readout"><span class="a-readout__tag">cel</span><span>${esc(v.tenantScope)}</span></div></div>` : ''}

      <div class="a-sim">
        <span>Failing it looks like</span>
        <span class="a-badge${operation === 'list' ? '' : ' a-badge--warn'}">${outcome.status}</span>
        <span class="a-sim__why"><strong>${esc(outcome.title)}.</strong> ${esc(outcome.detail)}</span>
      </div>

      ${v.hiddenFields.length ? `<div class="a-sim"><span>Not readable</span>
        <span class="a-badge a-badge--warn">${v.hiddenFields.length}</span>
        <span class="a-sim__why"><code class="a-mono">${v.hiddenFields.join('</code>, <code class="a-mono">')}</code> appear in no response and can be named in no filter — a filter over one is refused exactly as a filter over a field that does not exist.</span></div>` : ''}
      ${v.readOnlyFields.length ? `<div class="a-sim"><span>Readable, not writable</span>
        <span class="a-badge">${v.readOnlyFields.length}</span>
        <span class="a-sim__why"><code class="a-mono">${v.readOnlyFields.join('</code>, <code class="a-mono">')}</code></span></div>` : ''}
    ` : ''}

    <div class="p-note"><span class="p-note__tag">why there is no record picker</span>
      <span><code class="a-mono">ManagementPolicySimulation</code> takes no record id, deliberately: evaluating a predicate against a stored row needs a read, and the Management API has no data surface. A client that scored a row here would be a second policy evaluator — and the first time it disagreed with the engine, this screen would teach the wrong thing with total confidence. To check one row, open it in Data under your own credential and compare.</span></div>
    <div class="a-row"><a class="a-btn a-btn--sm" href="#/data/${e.name}">Open ${e.name} in Data</a></div>
  </div>`;
}

/* One operation's editor: every way in, and the tests that narrow each one. */
function ruleEditor(e, op, m) {
  const raw = m.raw !== null;
  const bs = m.branches || [];
  const fields = testable(e);

  const condRow = (b, bi, c, i) => {
    const f = e.fields.find((x) => x.name === c.field) || fields[0];
    const ops = OPERATORS[f.type] || OPERATORS.string;
    const values = valueOptions(e, f);
    const needsValue = !NO_VALUE.includes(c.op);
    const trap = c.op === '!=' && !f.required;
    return `<div class="a-cond">
      <span class="a-cond__join">${i ? 'and' : 'only if'}</span>
      <select class="a-cond__part" data-act="condfield" data-op="${op}" data-entity="${e.name}" data-b="${bi}" data-i="${i}" aria-label="Field">
        ${fields.map((x) => `<option${x.name === c.field ? ' selected' : ''}>${x.name}</option>`).join('')}</select>
      <select class="a-cond__part" data-act="condop" data-op="${op}" data-entity="${e.name}" data-b="${bi}" data-i="${i}" aria-label="Test">
        ${ops.map(([o, w]) => `<option value="${o}"${o === c.op ? ' selected' : ''}>${w}</option>`).join('')}</select>
      ${needsValue ? (values.length
        ? `<select class="a-cond__part" data-act="condvalue" data-op="${op}" data-entity="${e.name}" data-b="${bi}" data-i="${i}" aria-label="Value">
            ${values.map(([v, w]) => `<option value="${esc(v)}"${String(v) === String(c.value) ? ' selected' : ''}>${esc(w)}</option>`).join('')}</select>`
        : `<input class="a-cond__part" style="width:92px" value="${esc(c.value)}" data-act="condvalue" data-op="${op}" data-entity="${e.name}" data-b="${bi}" data-i="${i}" aria-label="Value">`) : ''}
      <button class="a-btn a-btn--sm a-btn--ghost" data-act="condremove" data-op="${op}" data-entity="${e.name}" data-b="${bi}" data-i="${i}" type="button" aria-label="Remove this condition">✕</button>
      ${trap ? `<div class="a-nulltrap" style="width:100%">⚠ <span>A record whose <code class="a-mono">${c.field}</code> is empty also passes this. A comparison against an empty value is <code class="a-mono">false</code>, and "is not" negates that to true. Writing <code class="a-mono">${c.field} != null</code> instead is refused outright by the compiler — use <em>is set</em>.</span></div>` : ''}
    </div>`;
  };

  const branch = (b, bi) => `${bi ? '<div class="a-branch__or">or</div>' : ''}
    <div class="a-branch">
      <div class="a-branch__head">
        <span class="a-badge a-badge--accent">${b.kind === 'role' ? b.role : `the record's ${b.field}`}</span>
        <span class="p-muted">${b.kind === 'role' ? 'anyone holding this role' : 'the signed-in caller it names'}</span>
        <button class="a-btn a-btn--sm a-btn--ghost" style="margin-left:auto"
          data-act="${b.kind === 'role' ? 'rulerole' : 'ruleowner'}" data-op="${op}" data-entity="${e.name}"
          ${b.kind === 'role' ? `data-role="${b.role}"` : `data-field="${b.field}"`}>Remove</button>
      </div>
      ${(b.conds || []).map((c, i) => condRow(b, bi, c, i)).join('')}
      ${fields.length ? `<div><button class="a-preset" style="border-style:dashed" data-act="condadd" data-op="${op}" data-entity="${e.name}" data-b="${bi}" type="button">
        ${(b.conds || []).length ? '+ another condition' : '+ narrow this'}</button></div>` : ''}
    </div>`;

  const context = e.fields.filter((f) => f.hidden !== true).map((f) => f.name);

  return `<div class="a-perm__editor a-form">
    ${raw ? '' : bs.length
      ? `<div><span class="a-label">Ways in<span class="a-label__hint">Any one of these admits the caller. Tick a cell in the matrix above to add one; narrow it here.</span></span>
          <div style="display:flex;flex-direction:column;gap:var(--space-2);margin-top:var(--space-2)">${bs.map(branch).join('')}</div></div>`
      : `<div class="a-error"><span class="a-error__title">Refused for everyone</span>
          <span class="a-error__detail">No way in is declared, so this operation is denied for every caller, an admin included.</span>
          <span class="a-error__fix">Tick a cell in the matrix above.</span></div>`}

    <div class="a-row">
      <span class="p-muted">${raw ? 'Hand-written. The controls cannot represent it.' : 'The controls above produce this expression exactly.'}</span>
      <button class="a-btn a-btn--sm a-btn--ghost" style="margin-left:auto" data-act="ruleraw" data-op="${op}" data-entity="${e.name}">${raw ? 'Back to the controls' : 'Write the expression myself'}</button>
    </div>

    ${raw ? `<textarea class="a-cel__input" rows="3" aria-label="CEL expression" data-act="rawcel" data-op="${op}" data-entity="${e.name}">${esc(m.raw)}</textarea>
      <div class="a-cel__context">it may read
        <span class="a-cel__token">@user.id</span><span class="a-cel__token">@user.roles</span><span class="a-cel__token">@tenant.id</span>
        <span>and these fields of this record:</span>
        ${context.map((n) => `<span class="a-cel__token">${n}</span>`).join('')}
        <span>There is no <code class="a-mono">@user.email</code>, no arithmetic, and <code class="a-mono">has()</code> is the only function. <code class="a-mono">== null</code> is refused — use <code class="a-mono">has()</code>.</span></div>
      ${rawDiagnostics(m.raw, e)}` : `
      <div class="a-readout"><span class="a-readout__tag">cel</span><span>${esc(celOf(m, e)) || '// no rule — refused for everyone'}</span></div>`}
  </div>`;
}

/* A dry run over the expression, on the rules the compiler actually enforces. It is not the
   compiler — it is the subset a client can check without one, and it says so. */
function rawDiagnostics(cel, e) {
  const problems = [];
  if (/[!=<>]=\s*null\b|\bnull\s*[!=]=/.test(cel)) {
    problems.push(['== null is rejected', 'A comparison against a null literal always evaluates to false under the two-valued null rule, which silently makes its negation always true. Use has(field) or !has(field).']);
  }
  const call = cel.match(/\b([a-z][a-zA-Z0-9_]*)\s*\(/);
  if (call && !['has'].includes(call[1])) {
    problems.push([`${call[1]}() is not a function here`, 'The Rule profile admits no function call but has(). endsWith, contains, matches and now do not exist in it.']);
  }
  if (/[+\-*/]/.test(cel.replace(/'[^']*'/g, ''))) {
    problems.push(['Arithmetic is not allowed in a rule', 'The Rule column of the profile table marks arithmetic ✗. It is admitted in Computed only.']);
  }
  if (/@user\.(?!id\b|roles\b)\w+/.test(cel)) {
    problems.push(['@user exposes id and roles, and nothing else', 'Typed claims are #37. An attribute gate — a mail domain, a team — is not expressible in this or any other block.']);
  }
  for (const m of cel.matchAll(/'([a-z][a-z0-9_-]*)'\s+in\s+@user\.roles/g)) {
    const known = [...declaredRoles(), ...BUILTIN_ROLES.map(([r]) => r)];
    if (!known.includes(m[1])) {
      problems.push([`'${m[1]}' is not a declared role`, `Role literals are validated at apply against auth.roles, with the same "did you mean" a typo in any rule gets. Declared: ${known.join(', ')}.`]);
    }
  }
  const names = new Set(e.fields.map((f) => f.name));
  for (const m of cel.matchAll(/\bhas\(([a-z][a-z0-9_]*)\)/g)) {
    if (!names.has(m[1])) problems.push([`${m[1]} is not a field of ${e.name}`, 'A rule sees this entity’s own fields and the closed @user / @tenant context. Nothing else is in scope.']);
  }

  if (!problems.length) {
    return `<div class="a-row" style="color:var(--ok-fg);font-size:var(--text-xs)">${icon('check')}
      <span>Nothing this client can check is wrong. The compiler is the authority — a real editor sends this through <code class="a-mono">PUT …?dryRun=true</code> on blur and renders what comes back.</span></div>`;
  }
  return problems.map(([title, detail]) => `<div class="a-error">
    <span class="a-error__title">${esc(title)}</span>
    <span class="a-error__detail">${esc(detail)}</span></div>`).join('');
}

/* ==========================================================================
   Preview — one preview, grouped by kind, with a plan that means what it says
   ========================================================================== */

function diffLines(before, after) {
  const a = before === undefined ? [] : JSON.stringify(before, null, 2).split('\n');
  const b = after === undefined ? [] : JSON.stringify(after, null, 2).split('\n');
  return [...a.map((t) => ['del', t]), ...b.map((t) => ['add', t])];
}

function diffBlock(rows) {
  return `<div class="a-diff">${rows.map(([k, t], i) => `<div class="a-diff__line${k === 'add' ? ' a-diff__line--add' : k === 'del' ? ' a-diff__line--del' : ''}">
    <span class="a-diff__gutter">${i + 1}</span><span>${k === 'add' ? '+' : k === 'del' ? '−' : ' '} ${esc(t)}</span></div>`).join('')}</div>`;
}

function screenPreview() {
  const groups = grouped();
  const migration = workingPlan();
  const level = myLevel();
  const needsAdmin = touchesAccess();
  const mayApply = level === 'admin' || (level === 'developer' && !needsAdmin);

  if (!groups.length) {
    return `${header([{ label: 'Schema', route: '#/schema' }, { label: 'Preview changes' }])}
    <div class="a-content"><div class="a-stack" style="max-width:960px">
      <div class="a-empty"><span class="a-empty__title">Nothing is waiting</span>
        <span class="a-empty__body">The working copy matches revision ${wc.revision}. Change a field, a rule or a role and it appears here with the migration it would run.</span>
        <a class="a-btn a-btn--primary" href="#/schema">Open the schema</a></div>
    </div></div>`;
  }

  const total = groups.reduce((n, g) => n + g.rows.length, 0);

  const planPanel = `<div class="a-panel">
      <div class="a-section"><span class="a-section-title">What runs against the database</span>
        <span class="a-section-sub">In this order, in one transaction.</span></div>
      ${migration.isEmpty
        ? `<div class="a-empty" data-plan="empty"><span class="a-empty__title">No migration step</span>
            <span class="a-empty__body">This apply changes <strong>policy, not storage</strong> — not "no changes". <code class="a-mono">plan.isEmpty</code> means the descriptor changes nothing about the schema, which a rules-only or roles-only edit does. The diff above is what changes; a new revision is still appended and the policy catalogue is re-primed from it.</span></div>`
        : migration.steps.map((s) => `<div class="a-row" style="align-items:flex-start;padding:var(--space-4);border-bottom:1px solid var(--border)" data-step${s.destructive ? ' data-destructive' : ''}>
            <span class="a-badge${s.destructive ? ' a-badge--danger' : ' a-badge--ok'}" style="flex:none;width:96px;justify-content:center">${s.destructive ? 'destroys' : 'safe'}</span>
            <span style="flex:1"><span style="font-size:var(--text-sm);font-family:var(--font-mono)">${esc(s.text)}</span>
              <span class="a-switcher-meta">${esc(s.loses ? `Loses ${s.loses}.` : s.note ?? 'Nothing stored is lost.')}</span></span></div>`).join('')}
    </div>`;

  /* Each step carries the entity it touches, so the word to type is a fact rather than the
     result of a regex over prose. Several entities losing data at once is one confirmation over
     the project name, because typing five names is a ritual rather than a check. */
  const destructiveEntities = [...new Set(migration.steps.filter((s) => s.destructive).map((s) => s.entity))];
  const confirmWord = destructiveEntities.length === 1 ? destructiveEntities[0] : wc.working.name;

  return `${header([{ label: 'Schema', route: '#/schema' }, { label: 'Preview changes' }])}
  <div class="a-content"><div class="a-stack" style="max-width:960px">
    <div><h1 class="a-page-title">${total} ${total === 1 ? 'change' : 'changes'}, nothing applied</h1>
      <p class="p-muted p-tight">This is <code class="a-mono">PUT ${mgmt('/projects/{project}/descriptor')}?dryRun=true</code> — the same call the CLI makes, through the same <code class="a-mono">PreviewAsync</code>. Nothing below has run.</p></div>

    ${state.applyState === 'stale' ? staleBanner() : ''}
    ${state.applyState === 'refused-destructive' ? destructiveRefusal(migration) : ''}

    ${groups.map((g) => `<div class="a-panel" data-group="${g.key}">
      <div class="a-section"><span class="a-section-title">${g.title}</span>
        <span class="a-section-sub">${esc(g.note)}</span>
        <span class="a-badge" style="margin-left:auto">${g.rows.length}</span></div>
      ${g.rows.map((c) => `<div style="padding:var(--space-4);border-bottom:1px solid var(--border)">
        <div class="a-row" style="margin-bottom:var(--space-2)">
          <span style="font-size:var(--text-sm);font-weight:var(--weight-medium)">${esc(c.label)}</span>
          <code class="a-mono" style="margin-left:auto;font-size:var(--text-2xs)">${esc(c.pointer)}</code></div>
        ${diffBlock(diffLines(c.before, c.after))}
      </div>`).join('')}
    </div>`).join('')}

    ${planPanel}

    ${migration.hasDestructiveChanges ? `<div class="a-panel" style="border-color:var(--danger-fg)" data-destructive-gate>
      <div class="a-section" style="border-color:var(--danger-fg)"><span class="a-section-title" style="color:var(--danger-fg)">This plan destroys data</span></div>
      <div style="padding:var(--space-5);display:flex;flex-direction:column;gap:var(--space-3)">
        <span class="p-muted">${migration.steps.filter((s) => s.destructive).map((s) => `<strong>${esc(s.text)}</strong> — loses ${esc(s.loses ?? 'stored values')}.`).join('<br>')}</span>
        <span class="p-muted"><code class="a-mono">allowDestructive</code> is never implied — not by this preview, and not by your management level. Saying yes here is what sets it.</span>
        <div class="a-confirm">
          <span style="font-size:var(--text-xs);color:var(--danger-fg);font-weight:var(--weight-medium)">Type <code class="a-mono" style="color:var(--danger-fg)">${esc(confirmWord)}</code> to allow it</span>
          <input class="a-input" placeholder="${esc(confirmWord)}" aria-label="Confirmation" data-act="confirmword" data-word="${esc(confirmWord)}"></div>
      </div>
    </div>` : ''}

    <div class="a-panel">
      <div class="a-section"><span class="a-section-title">Attribution</span>
        <span class="a-section-sub">Carried into the appended revision, and the only place <code class="a-mono">Author</code> and <code class="a-mono">Reason</code> ever become visible.</span></div>
      <div style="padding:var(--space-4)" class="a-form">
        <div class="a-field"><span class="a-label">Why<span class="a-label__hint">Configuration history shows this on the row forever.</span></span>
          <input class="a-input" id="apply-reason" placeholder="Widen quoted_price so larger quotes fit" value="${esc(state.applyReason ?? '')}" data-act="applyreason"></div>
        <span class="p-muted">Applying as <code class="a-mono">${esc(me().email)}</code>, against revision ${wc.revision} — sent as <code class="a-mono">If-Match: "${wc.revision}"</code>. An absent precondition is <code class="a-mono">428</code>, never a default; a stale one is <code class="a-mono">412</code>.</span>
      </div>
    </div>

    ${!mayApply ? `<div class="a-error" data-cannot-apply>
      <span class="a-error__title">You cannot apply this working copy</span>
      <span class="a-error__detail">${level === 'developer'
        ? 'It changes the <code class="a-mono">access</code> block, and both write members re-resolve the requirement to <strong>admin</strong> when that block differs from the applied one — otherwise a developer promotes itself by editing three lines of JSON.'
        : `Your level is <strong>${level ?? 'none'}</strong>. Applying a descriptor needs <strong>developer</strong>.`}</span>
      <span class="a-error__fix">${level === 'developer' ? 'Split the access change into its own apply for an administrator, or ask one to apply this.' : 'Ask an administrator for a level that a rule of the access block admits.'}</span>
    </div>` : ''}

    <div class="a-row">
      <button class="a-btn a-btn--ghost" data-act="go" data-route="#/schema">Back to the editor</button>
      <span style="margin-left:auto" class="p-hstack">
        <span class="p-muted">Applying writes revision ${wc.revision + 1}.</span>
        <button class="a-btn a-btn--primary" data-act="apply" ${mayApply ? '' : 'disabled aria-disabled="true"'}>Apply ${total === 1 ? 'this change' : 'these changes'}</button></span>
    </div>
  </div></div>`;
}

function staleBanner() {
  return `<div class="a-error" data-stale>
    <span class="a-error__title">Somebody else applied revision ${wc.revision} while you were editing</span>
    <span class="a-error__detail">Your <code class="a-mono">If-Match</code> named an older revision, so the apply was refused rather than overwriting theirs. This is <code class="a-mono">412 precondition-failed</code> — distinct from <code class="a-mono">428</code>, which means you sent no precondition at all.</span>
    <span class="a-error__fix">Re-preview against revision ${wc.revision}. Your edits are still here; the diff above is already computed against the new base.</span>
    <span class="a-error__type">https://alvo.dev/errors/precondition-failed</span>
    <div class="a-row" style="margin-top:var(--space-3)"><button class="a-btn a-btn--sm a-btn--primary" data-act="dismisserror">Re-preview against r${wc.revision}</button></div>
  </div>`;
}

function destructiveRefusal(migration) {
  return `<div class="a-error" data-refused-destructive>
    <span class="a-error__title">The plan discards data and you did not allow that</span>
    <span class="a-error__detail">${migration.steps.filter((s) => s.destructive).map((s) => esc(s.text)).join('; ')}. The guardrail is explicit in the API, so it is explicit here.</span>
    <span class="a-error__fix">Type the entity's name below to allow it, or change the descriptor so the plan keeps what it would drop.</span>
    <span class="a-error__type">https://alvo.dev/errors/destructive-change</span>
  </div>`;
}

/* ==========================================================================
   Import / export
   ========================================================================== */

function screenTransfer() {
  const n = count();
  return `${header([{ label: 'Schema', route: '#/schema' }, { label: 'Import / export' }])}
  <div class="a-content"><div class="a-stack" style="max-width:920px">
    <div><h1 class="a-page-title">The descriptor is the project</h1>
      <p class="p-muted p-tight" style="max-width:70ch">Everything you can click here lives in one JSON file. Export it into your repository and <code class="a-mono">alvo apply</code> reproduces this project exactly; import one and you get the diff before anything runs.</p></div>

    <div class="a-split">
      <div class="a-card">
        <span class="a-section-title">Export</span>
        <span class="p-muted">What comes back from <code class="a-mono">GET ${mgmt('/projects/{project}/descriptor')}</code> is <code class="a-mono">DescriptorVersion.DescriptorJson</code> — <strong>the stored text</strong>, not a re-serialisation. That is the only shape under which "everything clickable is exportable as code" is true without drift.</span>
        <div class="p-hstack">
          <button class="a-preset${!state.exportWorking ? ' a-preset--on' : ''}" data-act="exportpick" data-v="">As applied · r${wc.revision}</button>
          <button class="a-preset${state.exportWorking ? ' a-preset--on' : ''}" data-act="exportpick" data-v="working"${n ? '' : ' disabled'}>Working copy${n ? ` · ${n} unapplied` : ' (nothing waiting)'}</button>
        </div>
        <div class="a-row"><button class="a-btn a-btn--primary" data-act="overlay" data-kind="export">Show it</button></div>
      </div>
      <div class="a-card">
        <span class="a-section-title">Import</span>
        <span class="p-muted">Paste a descriptor. It is checked against the schema and dry-run against this database before you are asked to apply anything.</span>
        <textarea class="a-textarea" id="import-json" placeholder='{ "apiVersion": "alvo.dev/v1", "name": "…" }' aria-label="Descriptor JSON"></textarea>
        <div class="a-row"><button class="a-btn a-btn--primary" data-act="import">Check this descriptor</button></div>
        ${state.importError ? `<div class="a-error"><span class="a-error__title">${esc(state.importError.title)}</span>
          <span class="a-error__detail">${esc(state.importError.detail)}</span>
          <span class="a-error__type">https://alvo.dev/errors/validation</span></div>` : ''}
      </div>
    </div>

    <div class="a-panel">
      <div class="a-section"><span class="a-section-title">The four doors</span>
        <span class="a-section-sub">The same descriptor through any of them gives the same <code class="a-mono">SchemaModel</code>. A test measures it; the CLI door is the one that does not exist yet.</span></div>
      ${[['This dashboard', 'Clicks become the working copy you see beside every editor, and it is what apply receives.', true],
         ['The Management API', `PUT ${mgmt('/projects/{project}/descriptor')} — what this dashboard calls, in process.`, true],
         ['A repository file', 'GitOps: the file is the source, and boot applies it.', true],
         ['alvo apply', 'The CLI posts the same file to the same endpoint. #213 — it does not exist yet.', false]]
        .map(([k, v, live]) => `<div class="a-row" style="padding:var(--space-3) var(--space-5);border-bottom:1px solid var(--border)">
          <span style="flex:none;width:170px;font-size:var(--text-sm);font-weight:var(--weight-medium)">${k}</span>
          <span class="p-muted" style="flex:1">${v}</span>
          ${live ? '' : '<span class="a-notyet">Not yet</span>'}</div>`).join('')}
    </div>
  </div></div>`;
}

/* ==========================================================================
   Data — the ordinary Data API, under the operator's own context

   D4: no admin bypass exists, so this screen sees exactly what the caller's own rules permit. Two
   consequences the drawing used to hide:
     - a `tenancy: scoped` entity is a 403 for a caller with no tenant, BEFORE any rule runs;
     - a filter over a hidden field must be indistinguishable from one over a field that does not
       exist, because a hidden field's NAME is not public on the read surface.
   ========================================================================== */

function screenDataList() {
  const list = entities();
  const tenant = myTenant();
  return `${header([{ label: 'Data' }])}
  <div class="a-content"><div class="a-stack">
    <div><h1 class="a-page-title">Data</h1>
      <p class="p-muted p-tight" style="max-width:70ch">Records go through the same API and the same rules your application uses. Nothing here bypasses a policy — you see exactly what your own account is allowed to see, and no management level changes that.</p></div>
    ${tenant ? '' : tenantlessBanner()}
    <div class="a-cards">
      ${list.map((e) => {
        const blocked = e.tenancy === 'scoped' && !tenant;
        return `<div class="a-card${blocked ? '' : ' a-card--action'}" ${blocked ? '' : `data-act="go" data-route="#/data/${e.name}"`}>
        <span class="a-row"><span class="a-section-title" style="font-family:var(--font-mono)">${e.name}</span>
          <span class="a-badge" style="margin-left:auto">${e.tenancy}</span></span>
        <span style="font-size:var(--text-xl);font-weight:var(--weight-bold);font-variant-numeric:tabular-nums">${blocked ? '—' : num(ROW_COUNTS[e.name] ?? 0)}</span>
        <span class="p-muted">${blocked ? 'tenant-scoped, and you carry no tenant' : `${e.fields.length} fields${e.fields.filter((f) => f.hidden === true).length ? ` · ${e.fields.filter((f) => f.hidden === true).length} never returned` : ''}`}</span>
      </div>`;
      }).join('')}
    </div>
  </div></div>`;
}

function tenantlessBanner() {
  return `<div class="a-error" data-tenantless>
    <span class="a-error__title">You carry no tenant, so every tenant-scoped entity is refused</span>
    <span class="a-error__detail">The guard runs before any rule is consulted: a <code class="a-mono">tenancy: scoped</code> entity answers 403 to a caller with no tenant. This is not "your rules exclude every row" — that would be 200 with an empty page.</span>
    <span class="a-error__fix">An operator carries one tenant, granted on their own row in Access — the same way an API key carries the one it was issued for. <a href="#/access">Open Access</a> and grant yourself one.</span>
    <span class="a-error__type">https://alvo.dev/errors/forbidden</span>
  </div>`;
}

const visibleFields = (e) => e.fields.filter((f) => f.hidden !== true);

function columnsFor(e) {
  const chosen = state.columns?.[e.name];
  if (chosen) return visibleFields(e).filter((f) => chosen.includes(f.name));
  return visibleFields(e).slice(0, 6);
}

function rowsFor(e) {
  const tenant = myTenant();
  const all = ROWS[e.name] ?? [];
  return e.tenancy === 'scoped' ? all.filter((r) => r.tenant === tenant) : all;
}

function screenData(name) {
  const e = entityView(name) || entities()[0];
  if (!e) return screenDataList();
  const tenant = myTenant();

  if (e.tenancy === 'scoped' && !tenant) {
    return `${header([{ label: 'Data', route: '#/data' }, { label: e.name }])}
    <div class="a-content"><div class="a-stack">
      ${entityBar(e.name, '#/data')}
      <div><h1 class="a-page-title" style="font-family:var(--font-mono)">${e.name}</h1></div>
      ${tenantlessBanner()}
      <div class="p-note"><span class="p-note__tag">the distinction that matters</span>
        <span>403 here means <em>you carry no tenant</em>. 200 with an empty page means <em>your rules admit no row</em>. They look identical in a spinner and they need opposite fixes, which is why this screen never renders one as the other.</span></div>
    </div></div>`;
  }

  const rows = rowsFor(e);
  const cols = columnsFor(e);

  const inner = state.screenState === 'loading' ? skeletonRows()
    : state.screenState === 'empty' ? `<div class="a-empty">
        <span class="a-empty__title">No ${titleCase(e.name).toLowerCase()} match these filters</span>
        <span class="a-empty__body">One filter is active. Clear it to widen the search, or create the first record.</span>
        <span class="p-hstack"><button class="a-btn" data-act="state" data-state="ready">Clear filters</button>
        <button class="a-btn a-btn--primary" data-act="overlay" data-kind="record-new" data-id="${e.name}">New record</button></span></div>`
    : state.screenState === 'error' ? `<div style="padding:var(--space-5)"><div class="a-error" data-filter-error>
        <span class="a-error__title">The query names a field this entity does not expose</span>
        <span class="a-error__detail">Nothing about the shape of the request is guessable from this answer, and that is deliberate: a field the descriptor marks <code class="a-mono">hidden</code> and a field that was never declared are refused with <strong>one identical answer</strong>, because a hidden field's name is not public on the read surface.</span>
        <span class="a-error__fix">Check the field list in Schema for a name you may filter on.</span>
        <span class="a-error__type">https://alvo.dev/errors/malformed-query</span></div></div>`
    : rows.length === 0 ? `<div class="a-empty">
        <span class="a-empty__title">No records yet</span>
        <span class="a-empty__body">Nothing has been written to ${e.name}. Create the first one, or point an application at <code class="a-mono">POST /api/${e.name}</code>.</span>
        <button class="a-btn a-btn--primary" data-act="overlay" data-kind="record-new" data-id="${e.name}">New record</button></div>`
    : gridFor(e, rows, cols);

  const bulk = state.selectedRows.size ? `<div class="a-bulkbar">
      <span style="font-weight:var(--weight-medium)">${state.selectedRows.size} selected</span>
      ${inert('Set a field', 'A bulk patch is POST /api/{entity}/batch by id — drawn as a shape, not built here.')}
      <button class="a-btn a-btn--sm a-btn--danger" data-act="overlay" data-kind="bulk-delete">Delete</button>
      <button class="a-btn a-btn--sm a-btn--ghost" style="margin-left:auto" data-act="clear">Clear</button>
      <span class="p-muted" style="width:100%">The batch endpoint takes ids, so this covers the rows you selected on this page and no others. There is no delete-by-filter.</span></div>` : '';

  return `${header([{ label: 'Data', route: '#/data' }, { label: e.name }], `
      <button class="a-btn a-btn--primary" data-act="overlay" data-kind="record-new" data-id="${e.name}">${icon('plus')} New record</button>`)}
  <div class="a-content"><div class="a-stack">
    ${entityBar(e.name, '#/data', `<a class="a-btn a-btn--sm" href="#/schema/${e.name}">Edit fields</a>`)}

    <div class="p-between">
      <div><h1 class="a-page-title" style="font-family:var(--font-mono)">${e.name}</h1>
        <p class="p-muted p-tight">Reading as <strong>${esc(me().email)}</strong>, roles <code class="a-mono">${mintedRoles(me().id).join(', ')}</code>. A technician would see a shorter list, not an error.</p></div>
      <div class="p-hstack">
        ${e.tenancy === 'scoped'
          ? `<span class="a-badge a-badge--accent" title="The tenant discriminator this operator acts in. Alvo stores no name for a tenant — there is no tenant registry.">tenant ${esc(shortTenant(tenant))}</span>`
          : '<span class="a-badge">global — every tenant reads these rows</span>'}
        <button class="a-btn a-btn--sm" data-act="overlay" data-kind="columns" data-id="${e.name}">Columns (${cols.length}/${visibleFields(e).length})</button>
      </div>
    </div>

    <div class="a-grid-wrap">
      <div class="a-toolbar">
        <input class="a-input" style="max-width:200px" placeholder="Search ${e.name}" aria-label="Search" data-act="noop-search">
        <span class="a-chip" style="border-style:dashed;color:var(--dim)">+ Add filter</span>
        <span style="margin-left:auto" class="p-hstack">
          <span class="p-bar__label">state</span>
          ${['ready', 'loading', 'empty', 'error'].map((s) => `<button class="a-preset${state.screenState === s ? ' a-preset--on' : ''}" data-act="state" data-state="${s}">${s}</button>`).join('')}</span>
      </div>
      ${bulk}${inner}
      <div class="a-row" style="padding:var(--space-3) var(--space-4);border-top:1px solid var(--border)">
        <span class="p-muted">showing ${rows.length} of ${num(ROW_COUNTS[e.name] ?? rows.length)} · keyset paging over an opaque cursor — <strong>stable under concurrent writes</strong>, and the per-page cost grows with cursor depth rather than staying flat</span>
        <span style="margin-left:auto" class="p-hstack">
          <button class="a-btn a-btn--sm" disabled title="The cursor is opaque and forward-only: the API takes 'after', never 'before'. Going back is client-side history, and this drawing keeps none.">Previous</button>
          <button class="a-btn a-btn--sm" data-act="page" data-dir="next">Next</button></span></div>
    </div>

    ${e.fields.some((f) => f.hidden === true) ? `<div class="p-note"><span class="p-note__tag">hidden fields</span>
      <span><code class="a-mono">${e.fields.filter((f) => f.hidden === true).map((f) => f.name).join('</code>, <code class="a-mono">')}</code> are in no response and nameable in no filter. ${e.fields.some((f) => f.hidden === true && f.required) ? 'One of them is <strong>required</strong>, so it appears on the create form and in the write schemas — the single case a hidden field’s name is published at all.' : ''}</span></div>` : ''}
  </div></div>`;
}

function gridFor(e, rows, cols) {
  const cell = (r, f) => {
    const v = r[f.name];
    if (v === null || v === undefined || v === '') return '<span class="p-muted">—</span>';
    if (f.type === 'enum') return statusBadge(v);
    if (f.type === 'decimal') return eur(v);
    if (f.type === 'boolean') return v ? 'yes' : 'no';
    if (f.type === 'ref') {
      const target = ROWS[f.entity]?.find((x) => x.id === v);
      return `<a style="color:var(--accent)" href="#/data/${f.entity}">${esc(target?.name ?? target?.code ?? v)}</a>`;
    }
    if (f.type === 'uuid') {
      const u = USERS.find((x) => x.id === v);
      return `<span class="a-mono" title="${esc(v)}">${esc(u ? u.email.split('@')[0] : `${String(v).slice(0, 4)}…`)}</span>`;
    }
    if (f.type === 'json') return '<span class="p-muted">{ … }</span>';
    return esc(String(v));
  };

  const numeric = (f) => ['integer', 'decimal'].includes(f.type);

  return `<table class="a-grid">
      <thead><tr><th style="width:44px"></th>${cols.map((f) => `<th${numeric(f) ? ' class="a-num"' : ''}>${f.name}</th>`).join('')}</tr></thead>
      <tbody>${rows.map((r) => {
        const on = state.selectedRows.has(r.id);
        return `<tr aria-selected="${on}">
          <td><span class="a-check${on ? ' a-check--on' : ''}" role="checkbox" tabindex="0" aria-checked="${on}" data-act="pick" data-id="${r.id}" aria-label="Select this row">${on ? icon('check') : ''}</span></td>
          ${cols.map((f, i) => `<td${numeric(f) ? ' class="a-num"' : ''}${i === 0 ? ` data-act="overlay" data-kind="record" data-id="${r.id}" data-entity="${e.name}" style="cursor:pointer"` : ''}>${cell(r, f)}</td>`).join('')}
        </tr>`;
      }).join('')}</tbody></table>
    ${rows.map((r) => `<div class="a-row-card" data-act="overlay" data-kind="record" data-id="${r.id}" data-entity="${e.name}" tabindex="0">
      <div class="a-row-card__head"><span>${esc(r[cols[0]?.name] ?? r.id)}</span>${r.status ? statusBadge(r.status) : ''}</div>
      <div class="a-row-card__meta">${cols.slice(1, 4).map((f) => `<span>${String(r[f.name] ?? '—').slice(0, 24)}</span>`).join('')}</div>
    </div>`).join('')}`;
}

function skeletonRows() {
  return `<div style="padding:var(--space-4);display:flex;flex-direction:column;gap:var(--space-4)">
    ${Array.from({ length: 6 }, (_, i) => `<div class="a-row" style="gap:var(--space-4)">
      <div class="a-skeleton" style="height:14px;width:${34 - i * 2}%"></div>
      <div class="a-skeleton" style="height:14px;width:16%"></div>
      <div class="a-skeleton" style="height:14px;width:12%;margin-left:auto"></div></div>`).join('')}</div>`;
}

function statusBadge(s) {
  const map = { completed: 'a-badge--ok', in_progress: 'a-badge--accent', cancelled: 'a-badge--danger', priority: 'a-badge--accent' };
  return `<span class="a-badge ${map[s] || ''}">${esc(String(s).replace(/_/g, ' '))}</span>`;
}

/* --- The record detail, and the form that writes one ---------------------- */

function recordDrawer(entityName, id) {
  const e = entityView(entityName) || entities()[0];
  const r = (ROWS[e.name] ?? []).find((x) => x.id === id) ?? (ROWS[e.name] ?? [])[0];
  if (!r) return '<div class="a-drawer"><div class="a-empty"><span class="a-empty__body">No such record.</span></div></div>';

  const shown = visibleFields(e);
  const hidden = e.fields.filter((f) => f.hidden === true);

  const value = (f) => {
    const v = r[f.name];
    if (v === null || v === undefined || v === '') return '<span class="p-muted">— not set</span>';
    if (f.type === 'decimal') return `<span style="font-variant-numeric:tabular-nums">${eur(v)}</span>`;
    if (f.type === 'json') return `<code class="a-mono">${esc(JSON.stringify(v))}</code>`;
    if (f.type === 'uuid') {
      const u = USERS.find((x) => x.id === v);
      return `<span>${esc(u ? u.email : v)}</span> <span class="p-muted">${esc(v)}</span>`;
    }
    if (f.type === 'ref') {
      const target = ROWS[f.entity]?.find((x) => x.id === v);
      return `<a style="color:var(--accent)" href="#/data/${f.entity}">${esc(target?.name ?? target?.code ?? v)}</a>`;
    }
    if (f.type === 'enum') return statusBadge(v);
    return esc(String(v));
  };

  /* The reverse relation: every entity whose ref field points at this one. */
  const reverse = entities().flatMap((o) => o.fields
    .filter((f) => f.type === 'ref' && f.entity === e.name)
    .map((f) => ({ entity: o, field: f, rows: (ROWS[o.name] ?? []).filter((x) => x[f.name] === r.id) })));

  return `<div class="a-drawer a-drawer--wide" role="dialog" aria-modal="true" aria-label="Record detail"><div class="a-stack">
    <div class="p-between">
      <div><span class="a-page-title" style="font-size:var(--text-lg)">${esc(r.title ?? r.name ?? r.code ?? r.id)}</span>
        <p class="a-mono p-tight">${esc(e.name)} · ${esc(r.id)}</p></div>
      <button class="a-btn a-btn--sm a-btn--ghost" data-act="close">Close</button>
    </div>

    <div class="p-hstack">${r.status ? statusBadge(r.status) : ''}
      ${e.audit ? `<span class="a-badge" title="The row version. It is what an ETag is minted from, so a write can be made conditional with If-Match.">version ${r.version ?? 1}</span>`
        : '<span class="a-badge" title="This entity is not audited, so its rows carry no version: no ETag is minted and an If-Match naming one is refused rather than ignored.">no version</span>'}</div>

    <dl class="p-kv">
      ${shown.map((f) => `<dt>${f.name}</dt><dd>${value(f)}${f.readOnly === true ? ' <span class="a-badge">read only</span>' : ''}${f.computed ? ' <span class="a-badge a-badge--accent">computed</span>' : ''}${f.rollup ? ' <span class="a-badge a-badge--accent">rollup</span>' : ''}</dd>`).join('')}
    </dl>

    ${reverse.filter((x) => x.rows.length).map((x) => `<div class="a-field">
      <span class="a-label">${x.entity.name} pointing here<span class="a-label__hint">The reverse of <code class="a-mono">${x.entity.name}.${x.field.name}</code>. Nothing extra is declared to get this list — and it is what makes a <code class="a-mono">rollup</code> over them possible.</span></span>
      <div class="a-subgrid">
        <div class="a-subgrid__head"><span>${x.rows.length} ${x.entity.name}</span>
          <a class="a-btn a-btn--sm a-btn--ghost" style="margin-left:auto" href="#/data/${x.entity.name}">Open in Data</a></div>
        ${x.rows.map((s) => `<div class="a-subgrid__row" data-act="overlay" data-kind="record" data-id="${s.id}" data-entity="${x.entity.name}" tabindex="0">
          <span class="a-mono">${esc(s.reference ?? s.code ?? s.id)}</span>
          <span style="flex:1;min-width:0;overflow:hidden;text-overflow:ellipsis;white-space:nowrap">${esc(s.title ?? s.name ?? '')}</span>
          ${s.status ? statusBadge(s.status) : ''}</div>`).join('')}
      </div></div>`).join('')}

    ${hidden.length ? `<div class="a-panel" style="padding:var(--space-4)">
      <span class="a-label">${hidden.length} field${hidden.length > 1 ? 's are' : ' is'} not shown</span>
      <p class="p-muted p-tight" style="margin-top:var(--space-2)"><code class="a-mono">${hidden.map((f) => f.name).join('</code>, <code class="a-mono">')}</code> ${hidden.length > 1 ? 'are' : 'is'} hidden. Not withheld from you in particular — in no response Alvo sends, to anyone.</p></div>` : ''}

    <div class="a-row">
      <button class="a-btn a-btn--danger a-btn--sm" data-act="overlay" data-kind="delete-record" data-id="${r.id}" data-entity="${e.name}">Delete</button>
      <span style="margin-left:auto" class="p-hstack">
        <button class="a-btn a-btn--primary a-btn--sm" data-act="overlay" data-kind="record-new" data-id="${e.name}" data-edit="${r.id}">Edit</button></span>
    </div>
  </div></div>`;
}

/* The create form.

   Rebuilt from the descriptor: everything writable is on it, which means `readOnly`, `computed`
   and `rollup` are the ONLY exclusions. A hidden field is writable by design — `hidden` restricts
   reading and `readOnly` restricts writing — and a required hidden one must be on the form or its
   create is impossible. A `uuid` gets a text input, because the descriptor cannot say what it
   points at; a person picker over one would be the Invite defect again. */
function recordForm(entityName, editId) {
  const e = entityView(entityName) || entities()[0];
  const editing = editId ? (ROWS[e.name] ?? []).find((x) => x.id === editId) : null;
  const writable = e.fields.filter((f) => f.readOnly !== true && !f.computed && !f.rollup);
  const values = state.form.values;
  const errorFor = (name) => state.form.errors.find((x) => x.field === name);

  const control = (f) => {
    const v = values[f.name] ?? (editing ? editing[f.name] : undefined);
    const err = errorFor(f.name);
    const id = `rf-${f.name}`;
    const common = `id="${id}" data-act="formfield" data-field="${f.name}"${err ? ` aria-invalid="true" aria-describedby="err-${f.name}"` : ''}`;
    if (f.type === 'ref') return refPicker(e, f, v);
    if (f.type === 'enum') return `<div class="p-hstack" role="radiogroup" aria-labelledby="lbl-${f.name}">${f.values.map((o) => `<button class="a-preset${v === o ? ' a-preset--on' : ''}" type="button" data-act="formpick" data-field="${f.name}" data-value="${esc(o)}" role="radio" aria-checked="${v === o}">${esc(String(o).replace(/_/g, ' '))}</button>`).join('')}</div>`;
    if (f.type === 'text') return `<textarea class="a-textarea" ${common}>${esc(v ?? '')}</textarea>`;
    if (f.type === 'boolean') return `<span class="a-toggle${v ? ' a-toggle--on' : ''}" role="switch" tabindex="0" aria-checked="${!!v}" data-act="formtoggle" data-field="${f.name}"></span>`;
    if (f.type === 'json') return `<textarea class="a-textarea" style="font-family:var(--font-mono);font-size:var(--text-xs)" placeholder="{ }" ${common}>${esc(v ? JSON.stringify(v) : '')}</textarea>`;
    if (f.type === 'date') return `<input class="a-input" type="date" value="${esc(v ?? '')}" ${common}>`;
    if (f.type === 'datetime') return `<input class="a-input" type="datetime-local" value="${esc(String(v ?? '').replace(' ', 'T'))}" ${common}>`;
    if (f.type === 'uuid') return `<input class="a-input" style="font-family:var(--font-mono)" placeholder="00000000-0000-0000-0000-000000000000" value="${esc(v ?? '')}" ${common}>`;
    if (f.type === 'integer' || f.type === 'decimal') return `<input class="a-input" inputmode="decimal" value="${esc(v ?? '')}" ${common}>`;
    return `<input class="a-input" placeholder="${f.format ? formatHint(f.format) : ''}" value="${esc(v ?? '')}" ${common}>`;
  };

  return `<div class="a-drawer a-drawer--wide" role="dialog" aria-modal="true" aria-label="${editing ? 'Edit' : 'New'} record"><div class="a-form">
    <div class="p-between">
      <div><span class="a-page-title" style="font-size:var(--text-lg)">${editing ? 'Edit' : 'New'} ${titleCase(e.name).replace(/s$/, '')}</span>
        <p class="p-muted p-tight">Generated from the field types. Add a field in Schema and it appears here.</p></div>
      <button class="a-btn a-btn--sm a-btn--ghost" data-act="close">Close</button>
    </div>

    ${state.form.errors.filter((x) => !x.field).map((x) => `<div class="a-error">
      <span class="a-error__title">${esc(x.title)}</span>
      <span class="a-error__detail">${esc(x.detail)}</span>
      ${x.fix ? `<span class="a-error__fix">${esc(x.fix)}</span>` : ''}
      <span class="a-error__type">https://alvo.dev/errors/${esc(x.slug)}</span></div>`).join('')}

    ${writable.map((f) => {
      const err = errorFor(f.name);
      return `<div class="a-field">
      <span class="a-label" id="lbl-${f.name}">${f.name}${f.required ? ' <span style="color:var(--accent)" aria-label="required">∗</span>' : ''}
        <span style="font-weight:var(--weight-normal);color:var(--faint)"> · ${typeLabel(f)}</span>
        ${f.hidden === true ? `<span class="a-label__hint">Write only — you supply it and can never read it back. ${f.required ? 'It is required <em>and</em> hidden, which is the one case Alvo publishes a hidden field’s name: in the write schemas, so a create is possible at all.' : 'Optional and hidden, so its name is in no published schema — the document understates what a create will take, which is the safe direction.'}</span>` : ''}
        ${f.format ? `<span class="a-label__hint">must match <code class="a-mono">${esc(f.format)}</code>${declaredFormats().includes(f.format) ? ` — <code class="a-mono">${esc(wc.working.formats?.[f.format]?.pattern ?? '')}</code>, anchored over the whole value` : ''}</span>` : ''}
        ${f.type === 'uuid' ? '<span class="a-label__hint">A plain uuid. The descriptor does not say what it points at — only a <code class="a-mono">ref</code> does — so there is nothing to offer a picker over.</span>' : ''}</span>
      ${control(f)}
      ${err ? `<div class="a-error" id="err-${f.name}" style="margin-top:var(--space-2)">
        <span class="a-error__title">${esc(err.title)}</span>
        <span class="a-error__detail">${esc(err.detail)}</span>
        ${err.fix ? `<span class="a-error__fix">${esc(err.fix)}</span>` : ''}
        <span class="a-error__type">https://alvo.dev/errors/${esc(err.slug)} · violation code <code class="a-mono">${esc(err.code)}</code></span></div>` : ''}
    </div>`;
    }).join('')}

    <div class="a-row" style="position:sticky;bottom:0;background:var(--panel);padding-top:var(--space-3)">
      <button class="a-btn a-btn--ghost" data-act="close">Cancel</button>
      <span style="margin-left:auto" class="p-hstack">
        <span class="p-muted">${editing ? 'PATCH' : 'POST'} /api/${e.name}${editing ? '/{id}' : ''}</span>
        <button class="a-btn a-btn--primary" data-act="submitrecord" data-entity="${e.name}"${editing ? ` data-edit="${editing.id}"` : ''}>${editing ? 'Save' : `Create ${titleCase(e.name).replace(/s$/, '').toLowerCase()}`}</button></span>
    </div>
  </div></div>`;
}

const formatHint = (name) => ({ email: 'someone@example.com', uri: 'https://…', phone: '+421 900 000 000' }[name] ?? '');

/* A ref picker that starts collapsed. Three refs used to mean three 200 px lists open at once. */
function refPicker(e, f, chosen) {
  const target = ROWS[f.entity] ?? [];
  const display = displayFieldOf(f.entity);
  const picked = target.find((x) => x.id === chosen);
  const open = state.pickerOpen === f.name;

  if (picked && !open) {
    return `<div class="a-picker__chosen">${avatar(String(picked[display] ?? '?').slice(0, 1))}
      <span style="flex:1"><span style="font-weight:var(--weight-medium)">${esc(picked[display] ?? picked.id)}</span>
        <span class="a-switcher-meta">${esc(picked.id)}</span></span>
      <button class="a-btn a-btn--sm a-btn--ghost" data-act="pickopen" data-field="${f.name}">Change</button></div>`;
  }

  if (!open) {
    return `<button class="a-btn" data-act="pickopen" data-field="${f.name}" style="justify-content:flex-start">${icon('search')} Choose ${f.entity}…</button>`;
  }

  return `<div class="a-picker">
    <div class="a-picker__field">${icon('search')}
      <input class="a-input" style="border:none;padding:0;background:transparent" placeholder="Search ${f.entity}" aria-label="Search ${f.entity}" data-act="pickquery" data-field="${f.name}" value="${esc(state.pickQuery ?? '')}">
      <span class="p-muted" style="white-space:nowrap">${num(ROW_COUNTS[f.entity] ?? target.length)} records</span></div>
    <div class="a-picker__list" role="listbox">
      ${target.filter((x) => !state.pickQuery || String(x[display] ?? '').toLowerCase().includes(state.pickQuery.toLowerCase())).slice(0, 5).map((x) => `<button class="a-picker__item${x.id === chosen ? ' a-picker__item--on' : ''}" data-act="pick-ref" data-field="${f.name}" data-id="${x.id}" type="button" role="option" aria-selected="${x.id === chosen}">
        ${avatar(String(x[display] ?? '?').slice(0, 1))}
        <span><span style="font-weight:var(--weight-medium)">${esc(x[display] ?? x.id)}</span>
          <span class="a-switcher-meta">${esc(x.id)}</span></span></button>`).join('') || '<div class="p-muted" style="padding:var(--space-3)">Nothing matches.</div>'}
    </div>
    <div class="a-picker__field" style="border-top:1px solid var(--border)">
      <span class="p-muted">Searching <code class="a-mono">${display}</code>. <strong>The descriptor has no display-field concept</strong> — this picks the first required <code class="a-mono">string</code> field, and an entity without one falls back to the id. An <code class="a-mono">x-</code> hint would settle it properly.</span></div>
  </div>`;
}

/** The heuristic, stated wherever it is used: the first required string field, else the id. */
function displayFieldOf(entityName) {
  const e = entityView(entityName);
  const candidate = e?.fields.find((f) => f.type === 'string' && f.required && f.hidden !== true && !f.format)
    ?? e?.fields.find((f) => f.type === 'string' && f.hidden !== true);
  return candidate?.name ?? 'id';
}

/* ==========================================================================
   Access — four questions, and they are not the same question

   1. Who is this person            identity store         at once
   2. What roles do they hold       assignment: identity   at once
                                    catalogue: descriptor  an apply
   3. What may they reach here      access.*               an apply
   4. What may they do with data    entities.*.rules       an apply

   §3.7: membership creation joins the port, and the credential half stays inside the
   implementation that has one. So there IS a New person control, and it produces a real row.
   ========================================================================== */

function screenAccess() {
  const builtIn = BUILTIN_ROLES.map(([r]) => r);
  const catalogue = declaredRoles();
  const block = accessBlock();

  const people = USERS.map((u) => {
    const m = membershipOf(u.id);
    const lvl = effectiveLevel(u.id);
    const inertList = inertRolesOf(u.id);
    const self = u.id === state.signedIn;
    return `<tr data-person="${u.email}">
      <td><span class="a-mono" style="font-size:var(--text-sm);font-weight:var(--weight-medium)">${esc(u.email)}${self ? ' <span class="a-badge">you</span>' : ''}${u.bootstrap ? ' <span class="a-badge a-badge--ok">bootstrap</span>' : ''}</span>
        <span class="a-switcher-meta">${m.isDisabled ? 'disabled — resolves to no caller at all' : 'can sign in'}</span></td>
      <td>
        ${m.roleNames.map((r) => `<button class="a-badge${inertList.includes(r) ? ' a-role--inert' : ' a-badge--accent'}"
            ${self ? 'disabled aria-disabled="true" title="You cannot change your own roles"' : `data-act="unassign" data-id="${u.id}" data-role="${r}" title="Remove"`}
            type="button">${r}${self ? '' : ' ✕'}</button>`).join(' ')
          || '<span class="p-muted">no roles</span>'}
        ${self ? '' : `<button class="a-badge" style="border:1px dashed var(--border2);background:transparent" data-act="overlay" data-kind="assign" data-id="${u.id}" type="button" aria-label="Give a role">+</button>`}
        ${inertList.length ? `<div class="a-refused__reason" style="margin-top:var(--space-2)">⚠ <span><code class="a-mono">${inertList.join('</code>, <code class="a-mono">')}</code> ${inertList.length > 1 ? 'are' : 'is'} assigned and not declared, so ${inertList.length > 1 ? 'they match' : 'it matches'} nothing — anywhere, silently.
          <button class="a-btn a-btn--sm" data-act="declarerole" data-role="${inertList[0]}">Declare ${inertList[0]}</button></span></div>` : ''}
      </td>
      <td>${m.tenant
        ? `<code class="a-mono" title="${esc(m.tenant)}">${esc(shortTenant(m.tenant))}</code>`
        : '<span class="a-badge a-badge--warn">none</span>'}
        <button class="a-btn a-btn--sm a-btn--ghost" data-act="overlay" data-kind="tenant" data-id="${u.id}">Change</button></td>
      <td>${lvl ? `<span class="a-badge a-badge--ok">${lvl}</span>` : '<span class="a-badge">cannot open the dashboard</span>'}</td>
      <td><button class="a-btn a-btn--sm" data-act="person" data-id="${u.id}">What they can do</button></td>
    </tr>`;
  }).join('');

  return `${header([{ label: 'Access' }])}
  <div class="a-content"><div class="a-stack">
    <div><h1 class="a-page-title">Access</h1>
      <p class="p-muted p-tight" style="max-width:74ch">Two different things live here and they move at different speeds. <strong>Who holds which role, and which tenant they act in</strong> takes effect on the next request. <strong>Which roles exist, and what each unlocks</strong> is configuration — it changes the descriptor and waits for an apply, like any schema edit.</p></div>

    ${state.membershipLog.length ? `<div class="a-row" style="padding:var(--space-3) var(--space-4);border:1px solid var(--border);border-radius:var(--radius-md)" data-membership-log>
      <span class="a-badge a-badge--ok">done</span>
      <span class="p-muted">${state.membershipLog.slice(-3).map(esc).join(' · ')} — already in effect, and <strong>nothing recorded it</strong>. Audit is #42, in F7; a half-audit here would be a second, thinner answer to the question that issue owns.</span></div>` : ''}

    <div>
      <div class="a-band">
        <span class="a-band__title">People</span>
        <span class="a-band__when a-band__when--now">takes effect at once</span>
        <span class="a-band__note">Held in the identity store, not in the descriptor.</span>
      </div>
      <div class="a-panel">
        <div class="a-section"><span class="a-section-title">${USERS.length} people</span>
          <span class="a-section-sub">Search and paging are not drawn: <code class="a-mono">IAlvoUserStore.ListAsync</code> has neither, and that is a <strong>port</strong> gap rather than a screen one. §3.7 widens it.</span>
          <button class="a-btn a-btn--sm a-btn--primary" style="margin-left:auto" data-act="overlay" data-kind="new-person">${icon('plus')} New person</button></div>
        <table class="a-grid">
          <thead><tr><th>Signs in as</th><th>Roles they hold</th><th>Tenant</th><th>In this dashboard</th><th></th></tr></thead>
          <tbody>${people}</tbody></table>
        ${USERS.map((u) => `<div class="a-row-card" data-act="person" data-id="${u.id}" tabindex="0">
          <div class="a-row-card__head"><span class="a-mono">${esc(u.email)}</span><span class="a-badge">${effectiveLevel(u.id) ?? 'no level'}</span></div>
          <div class="a-row-card__meta">${membershipOf(u.id).roleNames.map((r) => `<span>${r}</span>`).join('') || '<span>no roles</span>'}</div></div>`).join('')}
        <div class="a-row" style="padding:var(--space-4) var(--space-5);color:var(--faint);font-size:var(--text-xs)">
          <span>Creating a person writes a membership row and, for <code class="a-mono">local</code> auth, mints a single-use token they set their own password with. You never type a colleague's password: the deployment refuses a bootstrap password as a <em>value</em> for the same reason. <strong>Nothing in this build delivers the token</strong> — no mail transport is configured for identity — so it is handed over out of band. Changing your own roles is refused server-side, not only here.</span>
        </div>
      </div>
    </div>

    <div>
      <div class="a-band">
        <span class="a-band__title">Roles and levels</span>
        <span class="a-band__when a-band__when--later">reviewed before it applies</span>
        <span class="a-band__note">Part of the descriptor, so every change here joins the one working copy.</span>
      </div>

      <div class="a-split">
        <div class="a-panel">
          <div class="a-section"><span class="a-section-title">Roles this project declares</span>
            <button class="a-btn a-btn--sm a-btn--primary" style="margin-left:auto" data-act="overlay" data-kind="new-role">${icon('plus')} New role</button></div>
          ${catalogue.length ? catalogue.map((r) => {
            const held = USERS.filter((u) => membershipOf(u.id).roleNames.includes(r)).length;
            const usedByRules = entities().filter((e) => OPS.some(([op]) => namesRole(e.rules?.[op], r))).map((e) => e.name);
            const usedByAccess = Object.entries(block).filter(([, p]) => namesRole(p, r)).map(([lvl]) => lvl);
            const blocked = usedByRules.length || usedByAccess.length;
            return `<div class="a-row" style="align-items:flex-start;padding:var(--space-4) var(--space-5);border-bottom:1px solid var(--border)">
              <span style="flex:1;min-width:0">
                <span style="font-size:var(--text-sm);font-weight:var(--weight-medium);font-family:var(--font-mono)">${r}</span>
                <span class="a-switcher-meta">${held} ${held === 1 ? 'person holds it' : 'people hold it'}${usedByRules.length ? ` · named in rules on ${usedByRules.join(', ')}` : ''}${usedByAccess.length ? ` · names the ${usedByAccess.join(' and ')} level` : ''}${blocked ? '' : ' · named in nothing'}</span></span>
              <button class="a-btn a-btn--sm a-btn--ghost" ${blocked
                ? `disabled aria-disabled="true" title="Named in ${usedByRules.length ? 'a rule' : ''}${usedByRules.length && usedByAccess.length ? ' and ' : ''}${usedByAccess.length ? 'an access level' : ''} — both are compiled at apply, so removing it here would make the apply refuse."`
                : `data-act="delrole" data-role="${r}"`}>Remove</button>
            </div>`;
          }).join('') : '<div class="a-empty"><span class="a-empty__body">This project declares no roles of its own. A rule can still name the three built-ins.</span></div>'}
          <div style="padding:var(--space-4) var(--space-5)">
            <span class="a-label">Always present, never declared</span>
            <div class="p-hstack" style="margin-top:var(--space-2)">${BUILTIN_ROLES.map(([r, what]) => `<span class="a-badge" title="${esc(what)}">${r}</span>`).join('')}</div>
            <p class="p-muted p-tight" style="margin-top:var(--space-2)"><code class="a-mono">anon</code> is every caller with no identity at all — naming it in a rule is how something becomes public. It cannot be assigned to anybody, and <code class="a-mono">authenticated</code> is appended to every signed-in caller automatically, so assigning that is a no-op the UI would be calling a grant.</p>
          </div>
        </div>

        <div class="a-stack">
          <div class="a-panel">
            <div class="a-section"><span class="a-section-title">Who may use this dashboard</span>
              <span class="a-section-sub">Three independent tests, highest match wins. They govern the dashboard and the Management API — never data.</span></div>
            ${LEVELS.map(([level, grants]) => {
              const predicate = block[level];
              return `<div style="padding:var(--space-4);border-bottom:1px solid var(--border)">
              <div class="a-row" style="margin-bottom:var(--space-2)"><span class="a-badge a-badge--accent">${level}</span>
                <span class="p-muted">${grants}</span></div>
              <div class="a-row">
                <input class="a-input" style="font-family:var(--font-mono);flex:1" value="${esc(predicate ?? '')}"
                  placeholder="'${catalogue[0] ?? 'admin'}' in @user.roles" data-act="setaccess" data-level="${level}" aria-label="${level} predicate">
                ${predicate ? `<button class="a-btn a-btn--sm a-btn--ghost" data-act="setaccess" data-level="${level}" data-value="">Clear</button>` : ''}</div>
              ${predicate ? accessDiagnostics(predicate) : ''}</div>`;
            }).join('')}
            <div style="padding:var(--space-4);color:var(--faint);font-size:var(--text-xs)">
              The <code class="a-mono">Access</code> CEL profile is closed: a literal, <code class="a-mono">@user</code>, <code class="a-mono">in</code>, comparison, <code class="a-mono">&amp;&amp; || !</code> — and nothing else. No field reference (a level sees no row), no <code class="a-mono">@tenant</code> (a level is project-scoped), no <code class="a-mono">has()</code>, no arithmetic.
              ${Object.keys(block).length === 0
                ? '<strong>This descriptor declares no level at all</strong>, so nobody but the deployment’s bootstrap administrator can manage the project. That is default-deny, and it is a usable state — it is just one person.'
                : 'Matching none of the three means the dashboard will not open. The bootstrap administrator is an admin whatever this block says.'}
            </div>
          </div>

          <div class="a-notyet-panel">
            <span class="a-notyet">Not yet</span>
            <span class="a-section-title">Teams</span>
            <span class="a-notyet-panel__body">A rule can name a role and the caller's own id. It cannot name a team, because <code class="a-mono">@user</code> exposes <code class="a-mono">id</code> and <code class="a-mono">roles</code> and nothing else — so a team here would let you draw a permission the engine could not enforce. Widening <code class="a-mono">@user</code> is additive (#37), so today's roles keep working when it lands.</span>
          </div>
        </div>
      </div>
      ${pendingBar()}
    </div>

    <div class="p-note"><span class="p-note__tag">two layers</span>
      <span>A role is a coarse answer — <em>Peter is a technician</em>. What a technician may do with a particular record is the fine one, and it lives in <a style="color:var(--accent)" href="#/rules">Rules</a>, per entity. They are complementary, not alternatives: without the second, a role either sees everything or nothing. <strong>What they can do</strong> on any row above shows both at once.</span></div>
  </div></div>`;
}

/** The Access profile is closed. This checks the constructs it excludes, and says who decides. */
function accessDiagnostics(predicate) {
  const problems = [];
  if (/@tenant/.test(predicate)) problems.push('@tenant is excluded — a level is project-scoped, so admitting it would make one predicate answer differently per request.');
  if (/\bhas\s*\(/.test(predicate)) problems.push('has() is excluded — a level sees no row, so there is no field to test the presence of.');
  if (/[+\-*/]/.test(predicate.replace(/'[^']*'/g, ''))) problems.push('Arithmetic is excluded from the Access profile.');
  const known = [...declaredRoles(), ...BUILTIN_ROLES.map(([r]) => r)];
  for (const m of predicate.matchAll(/'([a-z][a-z0-9_-]*)'/g)) {
    if (!known.includes(m[1])) problems.push(`'${m[1]}' is not declared in auth.roles — a level's role literals are validated at apply, exactly as a rule's are.`);
  }
  const bare = predicate.match(/\b(?!@|in\b|true\b|false\b)([a-z][a-z0-9_]*)\b(?!\s*\()/);
  if (bare && !/^(in)$/.test(bare[1]) && !predicate.includes(`'${bare[1]}'`)) {
    problems.push(`${bare[1]} reads like a field reference, and a level sees no row — the Access profile marks field references ✗, so this compiles in no profile rather than in every profile.`);
  }
  if (!problems.length) return '';
  return problems.map((p) => `<div class="a-error" style="margin-top:var(--space-2)"><span class="a-error__detail">${esc(p)}</span></div>`).join('');
}

/* One person, read downward through every layer that governs them. */
function personDrawer(id) {
  const u = userById(id);
  const m = membershipOf(u.id);
  const lvl = effectiveLevel(u.id);
  const minted = mintedRoles(u.id);
  const inertList = inertRolesOf(u.id);
  const caller = callerFor(u.id);

  return `<div class="a-drawer a-drawer--wide" role="dialog" aria-modal="true" aria-label="What this person can do"><div class="a-stack">
    <div class="p-between">
      <div><span class="a-page-title" style="font-size:var(--text-lg)">${esc(u.email)}</span>
        <p class="a-mono p-tight">${esc(u.id)}</p></div>
      <button class="a-btn a-btn--sm a-btn--ghost" data-act="close">Close</button>
    </div>

    <div class="a-ladder">
      <div class="a-rung${m.isDisabled ? '' : ' a-rung--on'}">
        <span class="a-rung__label">How they get in</span>
        <span class="a-rung__value">${m.isDisabled
          ? '<strong>Disabled.</strong> The resolver returns no caller at all — one of its five refusals — so every request is anonymous.'
          : `Signs in with a credential Alvo holds${u.bootstrap ? ', and is the deployment’s bootstrap administrator' : ''}.`}</span>
        ${u.bootstrap ? '<span class="a-rung__why">A bootstrap administrator has full management access whatever the access block says. That is deployment configuration, not descriptor — it cannot be granted or removed from this screen. It is <strong>not</strong> a data bypass: a tenant grant is separate, and without one they see global entities and nothing else.</span>' : ''}
      </div>

      <div class="a-rung${minted.length > 1 ? ' a-rung--on' : ''}">
        <span class="a-rung__label">What they are</span>
        <span class="a-rung__value">${minted.map((r) => `<span class="a-badge a-badge--accent">${r}</span>`).join(' ')}
          ${inertList.map((r) => `<span class="a-badge a-role--inert">${r}</span>`).join(' ')}</span>
        <span class="a-rung__why">Assigned ∩ declared, plus <code class="a-mono">authenticated</code>, which every signed-in caller carries.
          ${inertList.length ? `<strong>${inertList.join(', ')}</strong> is assigned and the descriptor does not declare it, so it is never minted and matches nothing — neither here nor in any rule. Nothing refuses it anywhere; it is simply quiet.` : ''}</span>
      </div>

      <div class="a-rung${m.tenant ? ' a-rung--on' : ''}">
        <span class="a-rung__label">Which tenant they act in</span>
        <span class="a-rung__value">${m.tenant ? `<code class="a-mono">${esc(m.tenant)}</code>` : '<strong>None.</strong>'}</span>
        <span class="a-rung__why">${m.tenant
          ? 'One tenant, confirmed rather than chosen — the same rule an API key follows. A request naming any other tenant resolves to no caller at all.'
          : 'Every <code class="a-mono">tenancy: scoped</code> entity answers 403 to them, before any rule is consulted. Global entities are unaffected.'}</span>
      </div>

      <div class="a-rung${lvl ? ' a-rung--on' : ''}">
        <span class="a-rung__label">In this dashboard</span>
        <span class="a-rung__value">${lvl
          ? `<strong>${lvl}</strong> — ${LEVELS.find(([l]) => l === lvl)[1].charAt(0).toLowerCase() + LEVELS.find(([l]) => l === lvl)[1].slice(1)}`
          : '<strong>Cannot open it.</strong> Every management operation is refused for them.'}</span>
        <span class="a-rung__why">${lvl && !u.bootstrap
          ? `Matched <code class="a-mono">${esc(accessBlock(wc.applied)[lvl])}</code>. Levels govern the dashboard and the Management API only — never data.`
          : u.bootstrap ? 'By bootstrap, not by the descriptor.'
          : `No access level admits any role they hold. The three predicates are: ${LEVELS.map(([l]) => `<code class="a-mono">${esc(accessBlock(wc.applied)[l] ?? '— not declared —')}</code>`).join(', ')}. Give them a role one of those names, or widen a level.`}</span>
      </div>

      <div class="a-rung a-rung--on">
        <span class="a-rung__label">With data</span>
        <span class="a-rung__why">Decided per entity by its rules, through the ordinary Data API — the same answer their own application gets. A dashboard level changes nothing here. This reads the <strong>applied</strong> descriptor, not your working copy.</span>
        ${entities().map((e) => {
          const cells = OPS.map(([op, verb]) => {
            const v = verdict(e.name, op, caller, wc.applied);
            if (!v.allowed) return { verb, v: 'no', note: CAUSES[v.cause].title.toLowerCase() };
            const model = parseCel((wc.applied.entities?.[e.name]?.rules ?? {})[op] ?? '');
            const byRole = (model.branches || []).find((b) => b.kind === 'role' && caller.roles.includes(b.role));
            if (byRole) return { verb, v: 'yes', note: (byRole.conds || []).length ? `while ${byRole.conds.map((c) => condCel(e, c)).join(' and ')}` : '' };
            const owner = (model.branches || []).find((b) => b.kind === 'owner');
            if (owner) return { verb, v: 'own', note: `only the rows ${owner.field} names them in${(owner.conds || []).length ? `, while ${owner.conds.map((c) => condCel(e, c)).join(' and ')}` : ''}` };
            return { verb, v: 'no', note: model.raw !== null ? 'hand-written rule — read it in Rules' : 'no branch admits them' };
          });
          const notes = cells.filter((c) => c.note).map((c) => `${c.verb.toLowerCase()}: ${c.note}`);
          return `<div style="margin-top:var(--space-3)">
            <div class="a-can a-can--head"><span>${e.name}</span>${cells.map((c) => `<span class="a-can__v">${c.verb.split(' ')[0]}</span>`).join('')}</div>
            <div class="a-can"><span class="p-muted">${num(ROW_COUNTS[e.name] ?? 0)} records</span>
              ${cells.map((c) => `<span class="a-can__v a-can__v--${c.v}">${c.v === 'yes' ? '✓' : c.v === 'own' ? 'own' : '—'}</span>`).join('')}
              ${notes.length ? `<span class="a-can__note">${esc(notes.join(' · '))}</span>` : ''}</div>
          </div>`;
        }).join('')}
      </div>
    </div>

    <div class="a-row" style="margin-top:auto">
      <a class="a-btn a-btn--sm" href="#/rules">Change what a role may do</a>
      <span style="margin-left:auto"><button class="a-btn a-btn--sm a-btn--ghost" data-act="close">Close</button></span>
    </div>
  </div></div>`;
}

/* ==========================================================================
   Configuration history, Integrations, Settings, Not-yet, Welcome
   ========================================================================== */

function screenHistory() {
  const a = wc.history.find((r) => r.revision === state.compareA);
  const b = wc.history.find((r) => r.revision === state.compareB);
  const diff = a && b ? revisionDiff(a.descriptor, b.descriptor) : [];

  return `${header([{ label: 'Configuration history' }])}
  <div class="a-content"><div class="a-stack">
    <div><h1 class="a-page-title">Configuration history</h1>
      <p class="p-muted p-tight" style="max-width:72ch">Every apply appends a revision recording who, when and why. Nothing here is edited or removed — undoing a change writes a <em>new</em> revision that points back at the one it restored.</p></div>

    <div class="p-note"><span class="p-note__tag">scope</span>
      <span>This is the history of the <em>configuration</em>. Who changed which record is a separate log; it does not exist yet (#42, F7) and joins as a second tab rather than being implied here.</span></div>

    <div class="a-split a-split--wide">
      <div class="a-panel">
        <div class="a-section"><span class="a-section-title">Revisions</span>
          <span class="a-section-sub">Comparing <strong>r${state.compareA}</strong> with <strong>r${state.compareB}</strong> — pick any two.</span></div>
        ${wc.history.map((r) => `<div class="a-row" style="align-items:flex-start;padding:var(--space-4);border-bottom:1px solid var(--border);${[state.compareA, state.compareB].includes(r.revision) ? 'background:var(--accentSoft)' : ''}" data-revision="${r.revision}">
          <span class="a-badge${r.rolledBackFrom ? ' a-badge--warn' : ''}" style="flex:none">r${r.revision}</span>
          <span style="flex:1;min-width:0"><span style="font-size:var(--text-sm)">${esc(r.reason)}</span>
            <span class="a-switcher-meta">${esc(r.author)} · ${r.at}${r.rolledBackFrom ? ` · restored r${r.rolledBackFrom}` : ''}</span></span>
          <span class="p-hstack" style="flex:none">
            <button class="a-btn a-btn--sm${state.compareA === r.revision ? ' a-btn--primary' : ''}" data-act="compare" data-side="a" data-rev="${r.revision}">A</button>
            <button class="a-btn a-btn--sm${state.compareB === r.revision ? ' a-btn--primary' : ''}" data-act="compare" data-side="b" data-rev="${r.revision}">B</button>
            ${r.revision !== wc.revision ? `<button class="a-btn a-btn--sm" data-act="overlay" data-kind="rollback" data-id="${r.revision}">Restore</button>` : '<span class="a-badge a-badge--ok">current</span>'}
          </span></div>`).join('')}
      </div>
      <div class="a-split__aside">
        <div class="a-row"><span class="a-section-title" style="font-size:var(--text-sm)">r${state.compareA} → r${state.compareB}</span>
          <span class="p-muted" style="margin-left:auto">${diff.length} ${diff.length === 1 ? 'difference' : 'differences'}</span></div>
        ${diff.length
          ? diff.map((d) => `<div style="margin-bottom:var(--space-3)">
              <code class="a-mono" style="font-size:var(--text-2xs)">${esc(d.pointer)}</code>
              ${diffBlock(diffLines(d.before, d.after))}</div>`).join('')
          : '<div class="a-empty"><span class="a-empty__body">These two revisions carry the same descriptor. A rollback appends a revision whose content equals an earlier one, so this is what a restore looks like from here.</span></div>'}
      </div>
    </div>
  </div></div>`;
}

/** The same pointer walk the working copy uses, over two stored descriptors. */
function revisionDiff(before, after) {
  const out = [];
  const walk = (x, y, pointer) => {
    if (JSON.stringify(x) === JSON.stringify(y)) return;
    const objects = x && y && typeof x === 'object' && typeof y === 'object' && !Array.isArray(x) && !Array.isArray(y);
    if (objects) {
      for (const key of new Set([...Object.keys(x), ...Object.keys(y)])) walk(x[key], y[key], `${pointer}/${key}`);
      return;
    }
    out.push({ pointer, before: x, after: y });
  };
  walk(before, after, '');
  return out;
}

function screenIntegrations() {
  const endpoints = wc.working.webhooks?.endpoints ?? [];
  const templates = Object.entries(wc.working.templates ?? {});

  /* Every action type that names an endpoint or a template, across every entity's hooks. */
  const usage = { endpoints: {}, templates: {} };
  for (const e of entities()) {
    for (const [point, list] of Object.entries(e.hooks ?? {})) {
      for (const h of list ?? []) {
        if (h.endpoint) (usage.endpoints[h.endpoint] ??= []).push(`${e.name} ${point}`);
        if (h.template) (usage.templates[h.template] ??= []).push(`${e.name} ${point}`);
      }
    }
  }

  return `${header([{ label: 'Integrations' }])}
  <div class="a-content"><div class="a-stack" style="max-width:960px">
    <div><h1 class="a-page-title">Integrations</h1>
      <p class="p-muted p-tight" style="max-width:72ch">Where a write leaves Alvo: an endpoint to post to, a message to send. Both are reachable from an entity's after-hooks today, and only from there.</p></div>

    <div class="a-notyet-panel">
      <span class="a-notyet">Not yet</span>
      <span class="a-section-title">What is declared here and does not fully run</span>
      <span class="a-notyet-panel__body">Two blocks, each partly honoured. This is the framework's own wording, served verbatim and never rewritten:</span>
      <span class="a-notyet-panel__consequence"><code class="a-mono">webhooks</code> — ${esc(warning('webhooks'))}</span>
      <span class="a-notyet-panel__consequence"><code class="a-mono">templates</code> — ${esc(warning('templates'))}</span>
    </div>

    <div class="a-panel">
      <div class="a-section"><span class="a-section-title">Webhook endpoints</span>
        <span class="a-section-sub">${endpoints.length ? 'Said once above, not repeated under each row.' : ''}</span>
        <button class="a-btn a-btn--sm a-btn--primary" style="margin-left:auto" data-act="overlay" data-kind="new-endpoint">${icon('plus')} New endpoint</button></div>
      ${endpoints.length ? endpoints.map((p) => `<div style="padding:var(--space-4);border-bottom:1px solid var(--border);display:flex;flex-direction:column;gap:var(--space-2)">
        <div class="a-row">
          <code class="a-mono" style="color:var(--text);font-size:var(--text-sm)">${esc(p.name)}</code>
          ${(usage.endpoints[p.name] ?? []).length ? `<span class="a-badge a-badge--ok">posted to by ${esc(usage.endpoints[p.name][0])}</span>` : '<span class="a-badge">nothing sends here</span>'}
          ${p.secretRef ? '<span class="a-badge a-badge--warn">not signed</span>' : ''}</div>
        <span class="p-muted" style="overflow-wrap:anywhere">${esc(p.url)}</span>
        ${p.secretRef ? `<span class="p-muted"><code class="a-mono">secretRef: ${esc(p.secretRef)}</code> — declared, and not read.</span>` : ''}
      </div>`).join('') : `<div class="a-empty"><span class="a-empty__title">No endpoint declared</span>
        <span class="a-empty__body">An endpoint is a name and a URL that an after-hook's <code class="a-mono">webhook</code> action can post to. Declaring one changes the descriptor, so it joins the working copy like any other edit.</span></div>`}
    </div>

    <div class="a-panel">
      <div class="a-section"><span class="a-section-title">Message templates</span>
        <button class="a-btn a-btn--sm a-btn--primary" style="margin-left:auto" data-act="overlay" data-kind="new-template">${icon('plus')} New template</button></div>
      ${templates.length ? templates.map(([name, t]) => `<div style="padding:var(--space-4);border-bottom:1px solid var(--border);display:flex;flex-direction:column;gap:var(--space-2)">
        <div class="a-row">
          <code class="a-mono" style="color:var(--text);font-size:var(--text-sm)">${esc(name)}</code>
          ${(usage.templates[name] ?? []).length ? `<span class="a-badge a-badge--ok">rendered by ${esc(usage.templates[name][0])}</span>` : '<span class="a-badge">nothing renders it</span>'}
          ${t.bodyFile ? '<span class="a-notyet">bodyFile — not read</span>' : ''}</div>
        <span class="p-muted">${esc(t.subject ?? '')}</span>
        ${t.bodyFile ? `<div class="a-refused__reason">⚠ <span>${esc(refusal('bodyFile').consequence)}</span></div>` : ''}
      </div>`).join('') : `<div class="a-empty"><span class="a-empty__title">No template declared</span>
        <span class="a-empty__body">A template is a subject and a body with <code class="a-mono">{{…}}</code> placeholders that an <code class="a-mono">email</code> action renders.</span></div>`}
      <div class="p-note" style="margin:var(--space-4)"><span class="p-note__tag">what "unused" means here</span>
        <span>A template an after-hook sends is rendered. A template referenced only from an automation rule is not, because no automation rule is evaluated — so "nothing renders it" on this screen does not mean unused in your descriptor.</span></div>
    </div>

    <div class="a-panel">
      <div class="a-section"><span class="a-section-title">What an action may be</span>
        <span class="a-section-sub">Seven types in <code class="a-mono">$defs/action</code>. Three are refused at apply, so no control offers them and each says why.</span></div>
      ${ACTION_TYPES.map((a) => `<div style="padding:var(--space-3) var(--space-5);border-bottom:1px solid var(--border)">
        <div class="a-row"><code class="a-mono" style="color:var(--text);font-size:var(--text-sm);width:120px;flex:none">${a.type}</code>
          <span class="p-muted" style="flex:1">${a.what}</span>
          <span class="a-badge${a.honoured ? ' a-badge--ok' : ' a-badge--danger'}">${a.honoured ? 'runs' : 'refused at apply'}</span></div>
        ${a.honoured ? '' : `<div class="a-refused__reason" style="margin-top:var(--space-2)">⚠ <span>${esc(refusal(a.type)?.consequence ?? '')}</span></div>
          <div class="a-refused__reason" style="color:var(--dim)"><span>→</span> <span>${esc(refusal(a.type)?.fix ?? '')}</span></div>`}
      </div>`).join('')}
    </div>

    <div class="a-panel">
      <div class="a-section"><span class="a-section-title">Events this project publishes</span>
        <span class="a-section-sub">One per entity and operation, in CloudEvents v1.0.2 shape.</span></div>
      ${entities().flatMap((e) => ['created', 'updated', 'deleted'].map((op) => `entity.${e.name}.${op}`)).slice(0, 6)
        .map((ev) => `<div class="a-row" style="padding:var(--space-3) var(--space-5);border-bottom:1px solid var(--border)">
          <code class="a-mono" style="color:var(--text);font-size:var(--text-sm)">${ev}</code>
          <span class="p-muted" style="margin-left:auto">in-process subscribers only</span></div>`).join('')}
      <div class="p-note" style="margin:var(--space-4)"><span class="p-note__tag">not yet</span>
        <span>There is no delivery log to show: a subscriber receives an event in process and nothing records that it did. A wildcard subscription is <strong>refused</strong> rather than accepted — ${esc(refusal('trigger.event').consequence)}</span></div>
    </div>
  </div></div>`;
}

function screenNotYet(key, title, lead, later) {
  return `${header([{ label: title }])}
  <div class="a-content"><div class="a-stack" style="max-width:860px">
    <div class="a-row"><h1 class="a-page-title p-tight">${title}</h1><span class="a-notyet">Not yet</span></div>
    <p class="p-muted p-tight" style="max-width:70ch">${lead}</p>
    <div class="a-notyet-panel">
      <span class="a-section-title">You can declare this, and it will not run</span>
      <span class="a-notyet-panel__body">A descriptor that declares <code class="a-mono">${key}</code> applies cleanly and earns a warning naming this block. Nothing is rejected — and nothing happens.</span>
      <span class="a-notyet-panel__consequence">${esc(warning(key))}</span>
      <span class="p-hstack"><button class="a-btn" data-act="go" data-route="#/schema/transfer">Declare it anyway</button></span>
    </div>
    <div class="p-note"><span class="p-note__tag">design</span><span>${later}</span></div>
  </div></div>`;
}

function screenSettings() {
  const level = myLevel();
  return `${header([{ label: 'Settings' }])}
  <div class="a-content"><div class="a-stack" style="max-width:920px">
    <div><h1 class="a-page-title">Settings</h1>
      <p class="p-muted p-tight">The surface a <code class="a-mono">developer</code> is excluded from. Most of it is not built, and this page says which parts.</p></div>

    <div class="a-panel">
      <div class="a-section"><span class="a-section-title">This instance</span>
        <span class="a-section-sub"><code class="a-mono">GET ${mgmt('/info')}</code> — four fields, and no more.</span></div>
      <div style="padding:var(--space-5)"><dl class="p-kv">
        <dt>version</dt><dd class="a-mono">${INFO.version}</dd>
        <dt>mode</dt><dd>${INFO.mode} — two values and no more, so an agent can branch on it</dd>
        <dt>dataProvider</dt><dd class="a-mono">${INFO.dataProvider}</dd>
        <dt>startupMode</dt><dd class="a-mono">${INFO.startupMode}</dd>
      </dl>
      <p class="p-muted p-tight" style="margin-top:var(--space-3)"><strong>There is no engine here, and there cannot be.</strong> <code class="a-mono">dataProvider</code> is the registered <code class="a-mono">IAlvoData</code> implementation's type name. The core may not reference the adapter that knows an engine's name — that is the provider-model principle — so reporting "PostgreSQL 16" would need either a <code class="a-mono">switch</code> over type names in the core or a port member whose only consumer is a diagnostic string.</p>
      </div>
    </div>

    <div class="a-panel">
      <div class="a-section"><span class="a-section-title">Multi-tenancy</span></div>
      <div style="padding:var(--space-5)"><dl class="p-kv">
        <dt>enabled</dt><dd>${tenancyEnabled() ? 'yes — scoped entities carry a tenant discriminator, global ones do not' : 'no'}</dd>
        <dt>your tenant</dt><dd>${myTenant() ? `<code class="a-mono">${esc(myTenant())}</code>` : '<span class="a-badge a-badge--warn">none</span>'}</dd>
        <dt>tenants</dt><dd class="p-muted">Not listable. Tenancy is resolved per request and <strong>no tenant registry exists</strong>; a list would have to come from <code class="a-mono">SELECT DISTINCT tenant_id</code>, which is both the data read the Management API refuses to have and a cross-tenant existence oracle.</dd>
      </dl></div>
    </div>

    <div class="a-panel">
      <div class="a-section"><span class="a-section-title">API keys</span>
        <span class="a-section-sub">A key's scopes gate the Data API. They gate <strong>nothing</strong> here — roles do.</span></div>
      <div class="a-notyet-panel" style="margin:var(--space-4)">
        <span class="a-notyet">Not yet</span>
        <span class="a-notyet-panel__body"><code class="a-mono">IAlvoUserStore</code>'s sibling for credentials, <code class="a-mono">IApiKeyStore</code>, is <code class="a-mono">FindAsync</code> and <code class="a-mono">TouchAsync</code> — it cannot issue a key and cannot revoke one — and <code class="a-mono">ManageApiKeys</code> has no HTTP route. So there is no New key control and no Revoke: either would be a button whose only possible output is nothing.</span>
        <span class="a-notyet-panel__consequence">What a key record does carry, when one exists: <code class="a-mono">User</code>, <code class="a-mono">RoleNames</code>, <code class="a-mono">Tenant</code>, <code class="a-mono">ExpiresAt</code>, <code class="a-mono">RevokedAt</code>. <strong>Roles are the security-relevant attribute</strong>: a key narrow enough to be refused by the Data API still reaches management the moment its roles satisfy a level. Narrow a key's management reach by narrowing its roles, never its scopes.</span>
      </div>
    </div>

    <div class="a-panel">
      <div class="a-section"><span class="a-section-title">Health</span></div>
      <div style="padding:var(--space-5)"><dl class="p-kv">
        <dt>ready</dt><dd><span class="a-badge a-badge--ok"><span class="a-dot"></span> ready</span> <code class="a-mono">/health/ready</code></dd>
        <dt>live</dt><dd><span class="a-badge a-badge--ok"><span class="a-dot"></span> live</span> <code class="a-mono">/health/live</code></dd>
      </dl></div>
    </div>

    <div class="a-panel">
      <div class="a-section"><span class="a-section-title">Danger zone</span></div>
      <div class="a-notyet-panel" style="margin:var(--space-4)">
        <span class="a-notyet">Not yet</span>
        <span class="a-notyet-panel__body">Deleting a project has no route either — <code class="a-mono">DeleteProject</code> is in the level table at <code class="a-mono">admin</code> and is one of the three operations with no HTTP surface. A Delete button here would be the same defect as a New key button.</span>
      </div>
    </div>

    <div class="p-note"><span class="p-note__tag">the assistant is not here</span>
      <span>The drawing carried a full assistant surface in this page. It is out of F5: it needs a secret store that does not exist, and <code class="a-mono">GET ${mgmt('/info')}</code> reports no AI connection to gate it on. The drawer's shape — it proposes a diff, leaves through the same <code class="a-mono">?dryRun=true</code> every other change uses, and never applies — is kept as a design and ships when there is something behind it.</span></div>
  </div></div>`;
}

/* ==========================================================================
   Welcome — first run

   §3.5: the bootstrap administrator already exists before the dashboard can be reached. There is
   no default password, and seeding never resets one. So step one is signing in, not creating an
   account — and the honest extra step this design adds is the tenant grant §2.7 needs.
   ========================================================================== */

function screenWelcome() {
  const step = Number(state.route.split('/')[2] || 1);
  const labels = ['Sign in', 'Name the project', 'Act in a tenant'];
  const steps = labels.map((label, i) => {
    const n = i + 1;
    const cls = n < step ? 'a-step--done' : n === step ? 'a-step--now' : '';
    return `<span class="a-step ${cls}"><span class="a-step__dot">${n < step ? '✓' : n}</span>${label}</span>${i < labels.length - 1 ? '<span class="a-step__rule"></span>' : ''}`;
  }).join('');

  const bodies = {
    1: `<div class="a-card a-form">
        <div><span class="a-section-title">Sign in as the bootstrap administrator</span>
          <p class="p-muted p-tight">This account already exists: the deployment configured <code class="a-mono">Alvo__Admin__BootstrapEmail</code> and mounted a password file, and it was seeded before this page could be reached. <strong>The image ships no credential and there is no default password</strong> — so there is nothing to change here, and nothing to be warned about.</p></div>
        <div class="a-field"><span class="a-label">Email</span>
          <input class="a-input" id="w-email" value="${esc(BOOTSTRAP.email)}" autocomplete="username"></div>
        <div class="a-field"><span class="a-label">Password<span class="a-label__hint">The one the mounted secret file holds. Rotating it afterwards is a dashboard operation; seeding never resets an account that exists.</span></span>
          <input class="a-input" id="w-pass" type="password" value="••••••••••••" autocomplete="current-password"></div>
        <div class="p-note"><span class="p-note__tag">if this is refused</span>
          <span>A caller who matches no access level and is not the bootstrap administrator is refused every management operation. The dashboard says so rather than showing an empty screen, and names the three predicates the descriptor declares — silent default-deny is the first support ticket.</span></div>
      </div>`,
    2: `<div class="a-card a-form">
        <div><span class="a-section-title">Name the project, or bring one</span>
          <p class="p-muted p-tight">A project is one descriptor. If you already have one, import it and you are finished here.</p></div>
        <div class="a-field"><span class="a-label">Project name</span>
          <input class="a-input" id="w-name" placeholder="field-service" value="${esc(state.wizardName ?? '')}" data-act="wizardname"></div>
        <div class="a-field"><span class="a-label">What it is<span class="a-label__hint">Becomes the descriptor's <code class="a-mono">description</code>, and the summary of the generated OpenAPI document.</span></span>
          <textarea class="a-textarea" id="w-desc" style="min-height:64px" data-act="wizarddesc">${esc(state.wizardDesc ?? '')}</textarea></div>
        <label class="a-row" style="gap:var(--space-3)"><span class="a-toggle${state.wizardTenancy ? ' a-toggle--on' : ''}" role="switch" tabindex="0" aria-checked="${!!state.wizardTenancy}" data-act="wizardtenancy"></span>
          <span><span style="font-size:var(--text-sm)">Separate each customer's data</span>
            <span class="a-switcher-meta">multi-tenancy — hard to add later, free now. Scoped entities carry a tenant discriminator; global ones do not.</span></span></label>
        <div class="a-row"><button class="a-btn" data-act="go" data-route="#/schema/transfer">I already have a descriptor</button></div>
      </div>`,
    3: `<div class="a-card a-form">
        <div><span class="a-section-title">Which tenant do you act in?</span>
          <p class="p-muted p-tight">You turned multi-tenancy on, so every scoped entity's rows carry a tenant — and a caller with none is refused them <em>before any rule runs</em>. An operator carries exactly one tenant, the same way an API key does. Without this step, Data is dead for every scoped entity on day one.</p></div>
        <div class="a-field"><span class="a-label">Your tenant<span class="a-label__hint">A uuid. Alvo stores no name for a tenant and has no registry — this is the discriminator your rows carry.</span></span>
          <input class="a-input" style="font-family:var(--font-mono)" id="w-tenant" value="${esc(state.wizardTenant ?? TENANTS[0].id)}" data-act="wizardtenant"></div>
        <div class="p-note"><span class="p-note__tag">not a bypass</span>
          <span>This grants data-path authority, not management authority. A bootstrap administrator is an <code class="a-mono">admin</code> whatever the descriptor says, and still sees only global entities until somebody grants them a tenant — including themselves.</span></div>
      </div>`,
  };

  const next = step < 3 ? `#/welcome/${step + 1}` : '#/schema';
  const canContinue = step !== 2 || (state.wizardName ?? '').trim().length > 0;

  return `<div class="a-wizard">
    <img src="alvo-wordmark.svg" width="148" height="34" alt="Alvo — backend as a service" style="display:block">
    <div>
      <h1 class="a-page-title" style="font-size:var(--text-2xl)">${['Nothing is configured yet', 'What are you building?', 'One more thing'][step - 1]}</h1>
      <p class="p-muted p-tight">${[
        'Alvo is running and the bootstrap administrator exists. Three steps and you are modelling.',
        'This becomes the descriptor — the one file that reproduces everything you do here.',
        'Because you chose multi-tenancy, and because nothing else can answer this for you.',
      ][step - 1]}</p>
    </div>
    <div class="a-steps">${steps}</div>
    ${bodies[step]}
    <div class="a-row">
      ${step > 1 ? `<button class="a-btn a-btn--ghost" data-act="go" data-route="#/welcome/${step - 1}">${icon('back')} Back</button>` : ''}
      <button class="a-btn a-btn--primary" style="margin-left:auto" data-act="${step < 3 ? 'go' : 'finishwizard'}" data-route="${next}"${canContinue ? '' : ' disabled aria-disabled="true"'}>
        ${step < 3 ? 'Continue' : 'Create it and start modelling'}</button>
    </div>
    ${step === 3 ? '<p class="p-muted p-tight">Your first entity comes next, in the schema editor — the same screen you will use to change it tomorrow.</p>' : ''}
  </div>`;
}

/* ==========================================================================
   Design notes
   ========================================================================== */

function screenNotes() {
  return `${header([{ label: 'Design notes' }])}
  <div class="a-content"><div class="a-stack" style="max-width:940px">
    <div><h1 class="a-page-title">What this prototype asks for</h1>
      <p class="p-muted p-tight" style="max-width:72ch">Two stylesheets load here. <code class="a-mono">alvo.css</code> is the repository's own, loaded from <code class="a-mono">src/MMLib.Alvo.Admin/wwwroot/</code> — not a copy. <code class="a-mono">proposed.css</code> is what this design adds: no new colour, no new token, only new components built from the ones that exist.</p></div>

    <div class="a-panel">
      <div class="a-section"><span class="a-section-title">Where the content comes from</span>
        <span class="a-section-sub">The first iteration's worst defects were all one defect: content written from memory. Nothing below is.</span></div>
      ${[
        ['Refusals and warnings', `${CAPABILITIES.refused.length} refused slots and ${CAPABILITIES.warned.length} warned blocks, verbatim`, 'UnhonouredFeatures.cs, UnhonouredSubsystems.cs'],
        ['Management routes', `${CAPABILITIES.routes.length} routes with the level each needs`, 'ManagementEndpoints.cs, ManagementOperations.cs'],
        ['Field types and facets', `${SCHEMA_FACETS.types.length} types, their required and optional facets, the three built-in formats`, 'schema/project.schema.json $defs/field'],
        ['The descriptor', 'The applied revision, byte for byte', 'examples/field-service/field-service.alvo.json'],
      ].map(([what, detail, source]) => `<div class="a-row" style="align-items:flex-start;padding:var(--space-3) var(--space-5);border-bottom:1px solid var(--border)">
        <span style="flex:none;width:190px;font-size:var(--text-sm);font-weight:var(--weight-medium)">${what}</span>
        <span class="p-muted" style="flex:1">${detail}</span>
        <code class="a-mono" style="font-size:var(--text-2xs);max-width:36%;text-align:right">${source}</code></div>`).join('')}
      <div class="p-note" style="margin:var(--space-4)"><span class="p-note__tag">how</span>
        <span><code class="a-mono">scripts/gen-prototype-fixtures</code> writes <code class="a-mono">generated/</code> from those files; <code class="a-mono">--check</code> fails when they drift. Nothing in that directory is edited by hand.</span></div>
    </div>

    <div class="a-panel">
      <div class="a-section"><span class="a-section-title">Decisions</span>
        <span class="a-section-sub">Stated so a later reader can tell a decision from an oversight. ${DECISIONS.filter((d) => d.reversal).length} of them reverse something an earlier version of this prototype did.</span></div>
      ${DECISIONS.map((d, i) => `<div style="padding:var(--space-4);border-bottom:1px solid var(--border)" id="d${i + 1}">
        <div class="a-row" style="align-items:flex-start"><span class="a-badge${d.reversal ? ' a-badge--warn' : ' a-badge--accent'}" style="flex:none">D${i + 1}</span>
          <span style="font-size:var(--text-sm);font-weight:var(--weight-medium)">${d.title}</span>
          ${d.reversal ? '<span class="a-badge" style="margin-left:auto;flex:none">reverses an earlier decision</span>' : ''}</div>
        <p class="p-muted p-tight" style="margin-top:var(--space-2);max-width:80ch">${d.why}</p>
        ${d.alternative ? `<p class="p-muted p-tight" style="margin-top:var(--space-2);max-width:80ch"><strong>The alternative, and why not:</strong> ${d.alternative}</p>` : ''}
        ${d.source ? `<p style="margin-top:var(--space-2)"><code class="a-mono" style="font-size:var(--text-2xs)">${d.source}</code></p>` : ''}
      </div>`).join('')}
    </div>

    <div class="a-panel">
      <div class="a-section"><span class="a-section-title">Review findings this iteration rejected</span>
        <span class="a-section-sub">Two adversarial reviews were largely right. These are the places they were not, with the reason.</span></div>
      ${REJECTED.map((r) => `<div style="padding:var(--space-4);border-bottom:1px solid var(--border)">
        <div class="a-row" style="align-items:flex-start"><span class="a-badge a-badge--danger" style="flex:none">${r.finding}</span>
          <span style="font-size:var(--text-sm);font-weight:var(--weight-medium)">${r.claim}</span></div>
        <p class="p-muted p-tight" style="margin-top:var(--space-2);max-width:80ch">${r.why}</p></div>`).join('')}
    </div>

    <div class="a-panel">
      <div class="a-section"><span class="a-section-title">The field editor changes with the type</span>
        <span class="a-section-sub">Not a design choice — <code class="a-mono">$defs/field</code> in the frozen schema, read by the generator.</span></div>
      <table class="a-grid">
        <thead><tr><th>Type</th><th>Required by the schema</th><th>Optional</th><th>What the editor shows</th></tr></thead>
        <tbody>${SCHEMA_FACETS.types.map((t) => {
          const needs = SCHEMA_FACETS.needs[t] ?? [];
          const optional = SCHEMA_FACETS.optional[t] ?? [];
          return `<tr>
            <td class="a-mono" style="color:var(--text)">${t}</td>
            <td>${needs.length ? needs.map((k) => `<span class="a-badge a-badge--accent">${k}</span>`).join(' ') : '<span class="p-muted">—</span>'}</td>
            <td>${optional.length ? optional.map((k) => `<span class="a-badge">${k}</span>`).join(' ') : '<span class="p-muted">—</span>'}</td>
            <td class="p-muted">${needs.length ? 'a block it will not let you leave empty' : optional.length ? 'ordinary fields' : 'nothing — and that is correct'}</td>
          </tr>`;
        }).join('')}</tbody>
      </table>
      <div class="p-note" style="margin:var(--space-4)">
        <span>The ${SCHEMA_FACETS.types.filter((t) => !(SCHEMA_FACETS.needs[t] ?? []).length && !(SCHEMA_FACETS.optional[t] ?? []).length).length} types that show nothing are right, not unfinished — <code class="a-mono">maxLength</code> on an integer is refused at apply. <code class="a-mono">decimal</code>, <code class="a-mono">enum</code> and <code class="a-mono">ref</code> have no valid descriptor without their settings, so those are a block rather than an optional section. And <code class="a-mono">computed</code> and <code class="a-mono">rollup</code> exclude each other, and either excludes <code class="a-mono">default</code> — which is moot, because <code class="a-mono">default</code> is refused outright.</span></div>
    </div>

    <div class="a-panel">
      <div class="a-section"><span class="a-section-title">Components</span>
        <span class="a-section-sub">What <code class="a-mono">proposed.css</code> asks to add to <code class="a-mono">alvo.css</code>.</span></div>
      <table class="a-grid"><thead><tr><th>Class</th><th>What it is</th><th>Status</th></tr></thead>
        <tbody>${COMPONENTS.map(([c, w, s]) => `<tr><td class="a-mono" style="font-size:var(--text-xs)">${c}</td><td>${w}</td>
          <td><span class="a-badge${s === 'new' ? ' a-badge--accent' : ''}">${s === 'new' ? 'to add' : 'in alvo.css'}</span></td></tr>`).join('')}</tbody></table>
    </div>

    <div class="a-panel">
      <div class="a-section"><span class="a-section-title">Still open</span>
        <span class="a-section-sub">Questions this iteration did not settle, and does not pretend to have.</span></div>
      ${OPEN_QUESTIONS.map((q) => `<div class="a-row" style="align-items:flex-start;padding:var(--space-3) var(--space-5);border-bottom:1px solid var(--border)">
        <span class="p-muted">${q}</span></div>`).join('')}
    </div>
  </div></div>`;
}

/* ==========================================================================
   Overlays
   ========================================================================== */

function overlay() {
  if (!state.overlay) return '';
  const { kind, id, entity: ent, edit } = state.overlay;
  const wrap = (pos, panel) => `<div class="p-overlay p-overlay--${pos}" data-overlay="${kind}"><div class="a-scrim" data-act="close"></div><div class="p-overlay__panel">${panel}</div></div>`;

  if (kind === 'palette') return wrap('top', palette());
  if (kind === 'person') return wrap('right', personDrawer(id));
  if (kind === 'record') return wrap('right', recordDrawer(ent, id));
  if (kind === 'record-new') return wrap('right', recordForm(id, edit));

  if (kind === 'whoami') {
    const level = myLevel();
    return wrap('center', modal('Signed in', `
      <dl class="p-kv">
        <dt>email</dt><dd class="a-mono">${esc(me().email)}</dd>
        <dt>roles minted</dt><dd><code class="a-mono">${mintedRoles(me().id).join(', ')}</code></dd>
        <dt>tenant</dt><dd>${myTenant() ? `<code class="a-mono">${esc(myTenant())}</code>` : '<span class="a-badge a-badge--warn">none — every scoped entity is 403</span>'}</dd>
        <dt>management level</dt><dd>${level ? `<span class="a-badge a-badge--ok">${level}</span>` : '<span class="a-badge">none</span>'}${isBootstrap(me().id) ? ' <span class="a-badge a-badge--ok">bootstrap — admin whatever the descriptor says</span>' : ''}</dd>
      </dl>
      <div class="a-field"><span class="a-label">Act as somebody else<span class="a-label__hint">A prototype affordance. In the product you are whoever the cookie says.</span></span>
        <div class="p-hstack">${USERS.map((u) => `<button class="a-preset${u.id === state.signedIn ? ' a-preset--on' : ''}" data-act="signin" data-id="${u.id}">${esc(u.email.split('@')[0])}</button>`).join('')}</div></div>`,
      '<button class="a-btn a-btn--ghost" data-act="close">Close</button>'));
  }

  if (kind === 'columns') {
    const e = entityView(id);
    const chosen = new Set(columnsFor(e).map((f) => f.name));
    return wrap('center', modal(`Columns on ${e.name}`, `
      <p class="p-muted p-tight">Becomes <code class="a-mono">?select=</code> on the read. Fewer columns is less to serialise, which is the point at ${e.fields.length} fields.</p>
      <div class="p-hstack">${visibleFields(e).map((f) => `<button class="a-preset${chosen.has(f.name) ? ' a-preset--on' : ''}" data-act="togglecolumn" data-entity="${e.name}" data-field="${f.name}">${f.name}</button>`).join('')}</div>
      <code class="a-code">GET /api/${e.name}?select=${[...chosen].join(',')}</code>`,
      '<button class="a-btn a-btn--primary" style="margin-left:auto" data-act="close">Done</button>'));
  }

  if (kind === 'export') {
    const doc = state.exportWorking ? wc.working : wc.applied;
    return wrap('center', modal(
      `Export ${esc(wc.working.name)}`,
      `<p class="p-muted p-tight">${state.exportWorking
        ? `The <strong>working copy</strong> — ${count()} unapplied change(s). This is what Apply would send, not what <code class="a-mono">GET …/descriptor</code> returns today.`
        : `Revision ${wc.revision}, exactly as applied. This is <code class="a-mono">DescriptorVersion.DescriptorJson</code> — the stored text, not a re-serialisation.`}</p>
      <pre class="a-json p-scroll" style="max-height:320px">${highlight(JSON.stringify(doc, null, 2))}</pre>`,
      `<button class="a-btn a-btn--ghost" data-act="close">Close</button>
       <span style="margin-left:auto" class="p-hstack">
         <button class="a-btn" data-act="copyexport">Copy</button></span>`));
  }

  if (kind === 'rollback') {
    const target = wc.history.find((r) => r.revision === Number(id));
    const preview = target ? revisionDiff(wc.applied, target.descriptor) : [];
    const losses = target ? workingPlanFor(wc.applied, target.descriptor) : { steps: [], hasDestructiveChanges: false };
    return wrap('center', modal(`Restore revision ${id}`, `
      <p class="p-muted p-tight">A restore does not rewind the history: it appends a <strong>new</strong> revision carrying revision ${id}'s descriptor, with <code class="a-mono">rolledBackFrom = ${id}</code>. Revision ${wc.revision} stays where it is.</p>
      <div class="a-panel" style="padding:var(--space-3)"><span class="a-label">${preview.length} difference(s) from r${wc.revision}</span>
        ${preview.slice(0, 4).map((d) => `<code class="a-mono" style="font-size:var(--text-2xs);display:block">${esc(d.pointer)}</code>`).join('')}
        ${preview.length > 4 ? `<span class="p-muted">…and ${preview.length - 4} more</span>` : ''}</div>
      ${losses.hasDestructiveChanges ? `<div class="a-error">
        <span class="a-error__title">The reverse migration discards data</span>
        <span class="a-error__detail">${losses.steps.filter((s) => s.destructive).map((s) => `${esc(s.text)} — loses ${esc(s.loses ?? 'stored values')}`).join('; ')}.</span>
        <span class="a-error__fix">A reverse migration routinely drops what the forward one added. <code class="a-mono">allowDestructive</code> is never implied by the route.</span></div>
      <div class="a-confirm">
        <span style="font-size:var(--text-xs);color:var(--danger-fg);font-weight:var(--weight-medium)">Type <code class="a-mono" style="color:var(--danger-fg)">${esc(wc.working.name)}</code> to confirm</span>
        <input class="a-input" placeholder="${esc(wc.working.name)}" aria-label="Confirmation" data-act="confirmword" data-word="${esc(wc.working.name)}"></div>` : '<p class="p-muted">Nothing stored is lost by this restore, so no name has to be typed.</p>'}`,
      `<button class="a-btn a-btn--ghost" data-act="close">Cancel</button>
       <button class="a-btn a-btn--danger" style="margin-left:auto" data-act="dorollback" data-id="${id}"${losses.hasDestructiveChanges ? ' disabled aria-disabled="true" data-needs-confirm' : ''}>Restore r${id}</button>`));
  }

  if (kind === 'bulk-delete' || kind === 'delete-record') {
    const n = kind === 'bulk-delete' ? state.selectedRows.size : 1;
    return wrap('center', modal(`Delete ${n} record${n > 1 ? 's' : ''}`, `
      <p class="p-muted p-tight">This goes through <code class="a-mono">${n > 1 ? `POST /api/{entity}/batch` : 'DELETE /api/{entity}/{id}'}</code> under your own credential, so a row your rules exclude answers 404 rather than being deleted. A <code class="a-mono">restrict</code>-ed reference refuses the delete with <code class="a-mono">409 conflict</code> and violation code <code class="a-mono">referenced</code>.</p>
      <div class="a-confirm">
        <span style="font-size:var(--text-xs);color:var(--danger-fg);font-weight:var(--weight-medium)">Type <code class="a-mono" style="color:var(--danger-fg)">delete ${n}</code> to confirm</span>
        <input class="a-input" placeholder="delete ${n}" aria-label="Confirmation" data-act="confirmword" data-word="delete ${n}"></div>`,
      `<button class="a-btn a-btn--ghost" data-act="close">Cancel</button>
       <button class="a-btn a-btn--danger" style="margin-left:auto" data-act="close" disabled aria-disabled="true" data-needs-confirm>Delete</button>`));
  }

  if (kind === 'assign') {
    const u = userById(id);
    const held = membershipOf(u.id).roleNames;
    /* `authenticated` is appended to every signed-in caller automatically, and `anon` is the
       absence of an identity. Offering either would be calling a no-op a grant. */
    const available = [...declaredRoles(), 'admin'].filter((r) => !held.includes(r));
    return wrap('center', modal(`Give ${esc(u.email)} a role`, `
      <p class="p-muted p-tight">Takes effect on their next request. It does not change the descriptor and <strong>nothing records it</strong> — audit is #42.</p>
      ${available.length ? `<div class="p-hstack">${available.map((r) => `<button class="a-preset" data-act="assign" data-id="${u.id}" data-role="${r}" ${r === 'admin' ? 'data-confirm="admin"' : ''} type="button">${r}${r === 'admin' ? ' ⚠' : ''}</button>`).join('')}</div>`
        : '<span class="p-muted">They already hold every role this project declares.</span>'}
      ${state.pendingGrant === 'admin' ? `<div class="a-error"><span class="a-error__title">Granting <code class="a-mono">admin</code></span>
        <span class="a-error__detail">The built-in administrator role. Every rule that names it admits them, and if a level names it they gain that level on their next request.</span>
        <div class="a-row" style="margin-top:var(--space-3)"><button class="a-btn a-btn--sm a-btn--danger" data-act="assign" data-id="${u.id}" data-role="admin" data-force="1">Grant it</button></div></div>` : ''}
      <span class="p-muted"><code class="a-mono">anon</code> and <code class="a-mono">authenticated</code> are not on this list: one is the absence of an identity, the other is appended to every signed-in caller. Assigning either is a no-op.</span>`,
      '<button class="a-btn a-btn--ghost" style="margin-left:auto" data-act="close">Cancel</button>'));
  }

  if (kind === 'tenant') {
    const u = userById(id);
    return wrap('center', modal(`Which tenant ${esc(u.email)} acts in`, `
      <p class="p-muted p-tight">One tenant, confirmed rather than chosen. A request naming any other tenant resolves to no caller at all — the same rule <code class="a-mono">TenantResolver</code> applies to an API key. There is no switcher, because a set of tenants is cross-tenant capability and that is a deliberate, audited grant deferred to #42.</p>
      <div class="a-field"><span class="a-label">Tenant<span class="a-label__hint">A uuid. Nothing stores a name for one.</span></span>
        <input class="a-input" style="font-family:var(--font-mono)" id="tenant-value" value="${esc(membershipOf(u.id).tenant ?? '')}" placeholder="00000000-0000-0000-0000-000000000000"></div>
      <div class="p-hstack">${TENANTS.map((t) => `<button class="a-preset" data-act="settenantvalue" data-value="${t.id}">${esc(t.short)}</button>`).join('')}
        <button class="a-preset" data-act="settenantvalue" data-value="">none</button></div>`,
      `<button class="a-btn a-btn--ghost" data-act="close">Cancel</button>
       <button class="a-btn a-btn--primary" style="margin-left:auto" data-act="settenant" data-id="${u.id}">Grant it</button>`));
  }

  if (kind === 'new-person') {
    return wrap('center', modal('New person', `
      <p class="p-muted p-tight">This writes a membership row through <code class="a-mono">IAlvoUserAdministration.CreateAsync</code> — an email, the roles they will hold, and the tenant they act in. No credential appears on that port.</p>
      <div class="a-field"><span class="a-label">Signs in as</span><input class="a-input" id="np-email" placeholder="colleague@field-service.sk" autofocus></div>
      <div class="p-note"><span class="p-note__tag">then what</span>
        <span>With <code class="a-mono">providers: ["local"]</code> they cannot sign in until a credential exists, so creating them mints a <strong>single-use set-password token</strong>. You never type their password — the deployment refuses a bootstrap password as a <em>value</em> for exactly that reason. <strong>Nothing here delivers the token</strong>: no mail transport is configured for identity, so it is shown once and handed over out of band.</span></div>
      ${state.credentialToken ? `<div class="a-reveal"><span class="a-reveal__value">${esc(state.credentialToken)}</span></div>
        <span class="p-muted">Shown once. It sets a password and then expires.</span>` : ''}`,
      `<button class="a-btn a-btn--ghost" data-act="close">Cancel</button>
       <button class="a-btn a-btn--primary" style="margin-left:auto" data-act="createperson">Create and mint a token</button>`));
  }

  if (kind === 'new-role') {
    return wrap('center', modal('New role', `
      <p class="p-muted p-tight">A role is a name you can hand to people and then name in a rule or a level. It carries no permissions of its own.</p>
      <div class="a-field"><span class="a-label">Name<span class="a-label__hint">Lower case, digits allowed — <code class="a-mono">${esc(SCHEMA_FACETS.identifierPattern)}</code>. <code class="a-mono">anon</code>, <code class="a-mono">authenticated</code> and <code class="a-mono">admin</code> already exist and cannot be redeclared.</span></span>
        <input class="a-input" id="nr-name" placeholder="billing-manager" style="font-family:var(--font-mono)" value="${esc(state.newRoleName ?? '')}" autofocus></div>
      <div class="p-note"><span class="p-note__tag">next</span>
        <span>Adding it changes the descriptor, so it joins the one working copy and needs an apply. Until then you can assign it to people and it will match nothing — silently, which is why an undeclared assigned role is marked inert on the people table.</span></div>`,
      `<button class="a-btn a-btn--ghost" data-act="close">Cancel</button>
       <button class="a-btn a-btn--primary" style="margin-left:auto" data-act="addrole">Add to the descriptor</button>`));
  }

  if (kind === 'new-entity') {
    return wrap('center', modal('New entity', `
      <p class="p-muted p-tight">Name it in the plural, the way you would say it out loud.</p>
      <div class="a-field"><span class="a-label">Name<span class="a-label__hint">Alvo adds <code class="a-mono">id</code> itself. The four audit columns arrive only with the toggle below.</span></span>
        <input class="a-input" id="ne-name" placeholder="invoices" value="${esc(state.newEntityName ?? '')}" autofocus></div>
      ${tenancyEnabled() ? `<div class="a-field"><span class="a-label">Who sees the records<span class="a-label__hint">Hard to change later — it decides whether every row carries a tenant discriminator.</span></span>
        <div class="p-hstack">
          <button class="a-preset${(state.newEntityTenancy ?? 'scoped') === 'scoped' ? ' a-preset--on' : ''}" data-act="netenancy" data-value="scoped">Each tenant sees only their own</button>
          <button class="a-preset${state.newEntityTenancy === 'global' ? ' a-preset--on' : ''}" data-act="netenancy" data-value="global">Everyone sees the same rows</button></div></div>` : ''}
      <label class="a-row" style="gap:var(--space-3)"><span class="a-toggle${state.newEntityAudit ? ' a-toggle--on' : ''}" role="switch" tabindex="0" aria-checked="${!!state.newEntityAudit}" data-act="neaudit"></span>
        <span><span style="font-size:var(--text-sm)">Keep a version on every row</span>
        <span class="a-switcher-meta"><code class="a-mono">audit: true</code> — adds <code class="a-mono">created_at</code>, <code class="a-mono">created_by</code>, <code class="a-mono">updated_at</code> and <code class="a-mono">updated_by</code>, and mints an ETag so a write can be made conditional</span></span></label>
      ${refusedControl('entity.softDelete', 'Keep deleted records recoverable')}`,
      `<button class="a-btn a-btn--ghost" data-act="close">Cancel</button>
       <button class="a-btn a-btn--primary" style="margin-left:auto" data-act="addentity">Create entity</button>`));
  }

  if (kind === 'new-index') {
    const e = entityView(id);
    const chosen = state.indexDraft ?? [];
    return wrap('center', modal(`Index on ${e.name}`, `
      <p class="p-muted p-tight">A composite index is ordered — the first column is the one a filter must name for it to be used.</p>
      <div class="p-hstack">${visibleFields(e).map((f) => `<button class="a-preset${chosen.includes(f.name) ? ' a-preset--on' : ''}" data-act="indexpick" data-field="${f.name}">${f.name}</button>`).join('')}</div>
      ${chosen.length ? `<code class="a-code">{ "fields": [${chosen.map((c) => `"${c}"`).join(', ')}] }</code>` : '<span class="p-muted">Pick at least one column.</span>'}`,
      `<button class="a-btn a-btn--ghost" data-act="close">Cancel</button>
       <button class="a-btn a-btn--primary" style="margin-left:auto" data-act="addindex" data-entity="${e.name}"${chosen.length ? '' : ' disabled aria-disabled="true"'}>Add it</button>`));
  }

  if (kind === 'new-hook') {
    const e = entityView(id);
    const draft = state.hookDraft ?? { point: 'beforeCreate', type: 'reject' };
    const chosen = ACTION_TYPES.find((a) => a.type === draft.type);
    return wrap('center', modal(`A hook on ${e.name}`, `
      <div class="a-field"><span class="a-label">When</span>
        <div class="p-hstack">${HOOK_POINTS.map(([p]) => `<button class="a-preset${draft.point === p ? ' a-preset--on' : ''}" data-act="hookpoint" data-value="${p}">${p}</button>`).join('')}</div>
        <span class="a-label__hint">${draft.point.startsWith('before') ? 'In the same transaction. It may refuse the write or change the values, and it reaches no network.' : 'After the commit, from the outbox, with retries. A failure never rolls back the write that caused it.'}</span></div>
      <div class="a-field"><span class="a-label">Do what</span>
        <div class="p-hstack">${ACTION_TYPES.map((a) => `<button class="a-preset${draft.type === a.type ? ' a-preset--on' : ''}" data-act="hooktype" data-value="${a.type}" data-honoured="${a.honoured}"${a.honoured ? '' : ' disabled aria-disabled="true" title="Refused at apply"'}>${a.type}${a.honoured ? '' : ' ⚠'}</button>`).join('')}</div>
        <span class="a-label__hint">${chosen?.what ?? ''}</span></div>
      ${chosen && !chosen.honoured ? `<div class="a-refused__reason">⚠ <span>${esc(refusal(draft.type)?.consequence ?? '')}</span></div>` : ''}
      ${ACTION_TYPES.filter((a) => !a.honoured).map((a) => `<div class="a-refused__reason" style="color:var(--dim)"><code class="a-mono">${a.type}</code> — ${esc(refusal(a.type)?.fix ?? '')}</div>`).join('')}
      <div class="a-field"><span class="a-label">${draft.type === 'reject' ? 'Message' : draft.type === 'mutate' ? 'Set which field' : draft.type === 'email' ? 'Template' : 'Endpoint'}</span>
        <input class="a-input" id="hook-arg" value="${esc(state.hookArg ?? '')}" placeholder="${draft.type === 'reject' ? 'An emergency call-out must be priority 1 or 2.' : draft.type === 'mutate' ? 'completed_on' : 'job-scheduled'}"></div>
      <div class="a-field"><span class="a-label">Only when<span class="a-label__hint">The Condition profile: it sees <code class="a-mono">new.</code> and <code class="a-mono">old.</code>, <code class="a-mono">changed()</code>, and the closed context. On a create there is no <code class="a-mono">old.</code> at all. <code class="a-mono">== null</code> is refused — use <code class="a-mono">has()</code>.</span></span>
        <input class="a-input" style="font-family:var(--font-mono)" id="hook-when" value="${esc(state.hookWhen ?? '')}" placeholder="new.status == 'completed' && !has(old.completed_on)"></div>`,
      `<button class="a-btn a-btn--ghost" data-act="close">Cancel</button>
       <button class="a-btn a-btn--primary" style="margin-left:auto" data-act="addhook" data-entity="${e.name}">Add the hook</button>`));
  }

  if (kind === 'new-endpoint') {
    return wrap('center', modal('New webhook endpoint', `
      <div class="a-field"><span class="a-label">Name</span><input class="a-input" id="ep-name" placeholder="billing-system" autofocus></div>
      <div class="a-field"><span class="a-label">URL</span><input class="a-input" id="ep-url" placeholder="https://billing.internal/hooks/alvo"></div>
      <div class="a-field"><span class="a-label">Signing secret<span class="a-label__hint">Declared, and not read by this build.</span></span>
        <input class="a-input" id="ep-secret" placeholder="BILLING_HOOK_SECRET"></div>
      <div class="a-refused__reason">⚠ <span>${esc(warning('webhooks'))}</span></div>`,
      `<button class="a-btn a-btn--ghost" data-act="close">Cancel</button>
       <button class="a-btn a-btn--primary" style="margin-left:auto" data-act="addendpoint">Add to the descriptor</button>`));
  }

  if (kind === 'new-template') {
    return wrap('center', modal('New message template', `
      <div class="a-field"><span class="a-label">Name</span><input class="a-input" id="tp-name" placeholder="job-scheduled" autofocus></div>
      <div class="a-field"><span class="a-label">Subject<span class="a-label__hint"><code class="a-mono">{{…}}</code> placeholders over <code class="a-mono">new</code>, <code class="a-mono">old</code>, <code class="a-mono">event</code> and <code class="a-mono">@user.id</code>.</span></span>
        <input class="a-input" id="tp-subject" placeholder="Your job {{new.reference}} is booked"></div>
      <div class="a-field"><span class="a-label">Body</span><textarea class="a-textarea" id="tp-body" placeholder="Hello — {{new.title}} is scheduled for {{new.scheduled_for}}."></textarea></div>
      ${refusedControl('bodyFile', 'Take the body from a file instead', 'templates/job-scheduled.md')}
      ${refusedControl('email.data', "An email action's data block", '{ "customer": "{{new.customer_id}}" }')}`,
      `<button class="a-btn a-btn--ghost" data-act="close">Cancel</button>
       <button class="a-btn a-btn--primary" style="margin-left:auto" data-act="addtemplate">Add to the descriptor</button>`));
  }

  if (kind === 'projects') {
    return wrap('top', `<div class="a-palette">
      <div class="a-palette__item a-palette__item--active">${avatar('F')}${esc(wc.working.name)}<span class="a-badge a-badge--ok" style="margin-left:auto">current</span></div>
      <div class="a-palette__item"><span class="p-muted">One project per instance. <code class="a-mono">GET ${mgmt('/projects')}</code> answers a list so the wire shape does not change when a second becomes possible, and this build serves exactly one.</span></div>
    </div>`);
  }

  return '';
}

function modal(title, body, footer) {
  return `<div class="a-modal" role="dialog" aria-modal="true" aria-label="${esc(title)}"><div class="a-stack a-form">
    <div><span class="a-section-title">${title}</span></div>
    ${body}
    <div class="a-row">${footer}</div>
  </div></div>`;
}

/** A plan between any two documents — what a rollback's reverse migration would do. */
const workingPlanFor = (before, after) => planBetween(before, after);

function palette() {
  const q = state.paletteQuery.trim().toLowerCase();
  const generated = entities().flatMap((e) => [
    { label: `Edit ${e.name}`, hint: 'schema', route: `#/schema/${e.name}` },
    { label: `Browse ${e.name}`, hint: 'data', route: `#/data/${e.name}` },
    { label: `Who can do what to ${e.name}`, hint: 'rules', route: `#/rules/${e.name}` },
  ]);
  const items = [...PALETTE_ITEMS, ...generated]
    .filter((p) => !q || p.label.toLowerCase().includes(q) || p.hint.includes(q))
    .slice(0, 9);
  const index = Math.min(state.paletteIndex, Math.max(0, items.length - 1));

  return `<div class="a-palette" role="dialog" aria-modal="true" aria-label="Command palette">
    <input class="a-palette__input" id="palette-input" placeholder="Jump to a screen" value="${esc(state.paletteQuery)}"
      role="combobox" aria-expanded="true" aria-controls="palette-list" aria-activedescendant="palette-${index}">
    <div id="palette-list" role="listbox">
      ${items.length ? items.map((p, i) => `<div class="a-palette__item${i === index ? ' a-palette__item--active' : ''}" id="palette-${i}" role="option" aria-selected="${i === index}" data-act="go" data-route="${p.route}">
        ${icon('search')}<span>${esc(p.label)}</span><span class="a-kbd" style="margin-left:auto">${esc(p.hint)}</span></div>`).join('')
        : '<div class="a-palette__item"><span class="p-muted">Nothing matches.</span></div>'}
    </div>
  </div>`;
}

/* ==========================================================================
   Router
   ========================================================================== */

const ROUTES = {
  '#/overview': screenOverview,
  '#/schema': screenSchemaList,
  '#/schema/preview': screenPreview,
  '#/schema/transfer': screenTransfer,
  '#/data': screenDataList,
  '#/rules': () => screenRules(entities()[0]?.name),
  '#/access': screenAccess,
  '#/integrations': screenIntegrations,
  '#/history': screenHistory,
  '#/settings': screenSettings,
  '#/notes': screenNotes,
  '#/automations': () => screenNotYet('automation', 'Automations',
    'When something happens, do something — a job is completed, so the customer is emailed. The hooks on an entity already do a narrow version of this; automation is the general one, and its engine is not built.',
    'When it lands, this is a list of rules — a trigger, a condition, an ordered list of actions — with the dry run the schema editor has: pick a past event, see which rules would have fired and what each action would have sent.'),
  '#/functions': () => screenNotYet('functions', 'Functions',
    'Your own C# running inside Alvo, on a trigger or a schedule, with the same database context an endpoint has.',
    'When it lands, this lists functions with their trigger, their last run and their output — and the editor is a code pane, not a form. Nothing about it is drawn yet, because a code editor designed before the runtime exists is a guess.'),
};

function render() {
  const r = state.route;
  const app = $('#app');
  if (!app) return;

  if (r.startsWith('#/welcome')) {
    app.innerHTML = `<div class="p-frame">${screenWelcome()}</div>${overlay()}`;
    afterRender();
    return;
  }

  let screen;
  try {
    if (r.startsWith('#/schema/') && !ROUTES[r]) {
      state.entity = r.split('/')[2];
      screen = screenEntity(state.entity);
    } else if (r.startsWith('#/data/')) {
      state.entity = r.split('/')[2];
      screen = screenData(state.entity);
    } else if (r.startsWith('#/rules/')) {
      screen = screenRules(r.split('/')[2]);
    } else {
      screen = (ROUTES[r] || screenOverview)();
    }
  } catch (error) {
    /* A screen that throws must not blank the shell: that is how the Access page broke once. */
    screen = `${header([{ label: 'Something went wrong' }])}
      <div class="a-content"><div class="a-error">
        <span class="a-error__title">This screen could not be drawn</span>
        <span class="a-error__detail">${esc(error.message)}</span>
        <span class="a-error__fix">The shell is intact — pick another section, or reload.</span></div></div>`;
    queueMicrotask(() => { throw error; });   // still surfaces on the console, never silently
  }

  app.innerHTML = `<div class="a-shell p-frame">${sidebar()}<div class="a-main">${screen}${bottomnav()}</div></div>${overlay()}`;
  afterRender();
}

/* --- Focus, after every render -------------------------------------------- */

const FOCUSABLE = 'a[href],button:not([disabled]),input:not([disabled]),select:not([disabled]),textarea:not([disabled]),[tabindex]:not([tabindex="-1"])';

function afterRender() {
  const panel = $('.p-overlay__panel');
  if (panel) {
    const first = $('#palette-input', panel) ?? $(FOCUSABLE, panel);
    if (first && document.activeElement !== first) first.focus({ preventScroll: true });
  } else if (state.lastFocus) {
    const target = document.querySelector(state.lastFocus);
    if (target) target.focus({ preventScroll: true });
    state.lastFocus = null;
  }

  /* Keep a restored caret where the typist left it. */
  if (state.caret) {
    const el = document.querySelector(state.caret.selector);
    if (el && el.setSelectionRange) {
      el.focus({ preventScroll: true });
      try { el.setSelectionRange(state.caret.pos, state.caret.pos); } catch { /* not a text input */ }
    }
    state.caret = null;
  }

  const marked = $('.a-json__sel') ?? $('.a-json__hit');
  if (marked && state.scrollToMark) {
    marked.scrollIntoView({ block: 'center' });
    state.scrollToMark = false;
  }
}

/* A full re-render replaces the DOM, and a re-render triggered by `change` fires on BLUR — so
   clicking the next control destroys the node the click was heading for and the click is lost.
   A person who typed a length and then clicked a format chip lost the chip. So a text edit
   commits on `input` and refreshes only what it changes outside the control being typed in. */
function softRender() {
  const pane = $('[data-descriptor]');
  if (pane) {
    const scope = state.route.startsWith('#/schema/') ? state.entity : null;
    const fresh = document.createElement('div');
    fresh.innerHTML = descriptorPane(scope);
    pane.replaceWith(fresh.querySelector('[data-descriptor]'));
    $('[data-pane-header]')?.replaceWith(fresh.querySelector('[data-pane-header]'));
  }

  const n = count();
  const badge = $('[data-count="unapplied"]');
  if (badge) badge.textContent = `${n} unapplied`;
  else if (n) render();

  const bar = $('[data-pending]');
  if (bar) {
    const fresh = document.createElement('div');
    fresh.innerHTML = pendingBar();
    const next = fresh.querySelector('[data-pending]');
    if (next) bar.replaceWith(next);
    else bar.remove();
  } else if (n) {
    render();
  }
}

/* ==========================================================================
   Events
   ========================================================================== */

const rememberCaret = (el) => {
  if (!el || el.selectionStart == null) return;
  const selector = el.id ? `#${el.id}` : `[data-act="${el.dataset.act}"][data-field="${el.dataset.field ?? ''}"]`;
  state.caret = { selector, pos: el.selectionStart };
};

function openOverlay(kind, dataset = {}) {
  state.lastFocus = document.activeElement?.id ? `#${document.activeElement.id}` : null;
  state.overlay = { kind, id: dataset.id, entity: dataset.entity, edit: dataset.edit };
}

document.addEventListener('click', (ev) => {
  const el = ev.target.closest('[data-act]');
  if (!el || el.disabled) return;
  const act = el.dataset.act;
  const d = el.dataset;

  const actions = {
    noop: () => ev.preventDefault(),
    'noop-search': () => {},
    go: () => { ev.preventDefault(); state.overlay = null; location.hash = d.route; },
    close: () => { state.overlay = null; state.pendingGrant = null; render(); },
    overlay: () => { openOverlay(d.kind, d); render(); },
    tab: () => { state.tab = d.tab; state.selectedField = null; render(); },
    field: () => {
      state.selectedField = d.field;
      if (d.field === '__new') state.draftField = { name: '', type: 'string' };
      state.scrollToMark = true;
      render();
    },
    closefield: () => { state.selectedField = null; state.draftField = null; render(); },
    fieldfilter: () => {},
    state: () => { state.screenState = d.state; render(); },
    discard: () => { discardWorking(); state.rawOps?.clear(); render(); },
    clear: () => { state.selectedRows.clear(); render(); },
    page: () => {},
    person: () => { openOverlay('person', d); render(); },
    signin: () => { state.signedIn = d.id; state.overlay = null; render(); },
    dismisserror: () => { state.applyState = null; render(); },
    exportpick: () => { state.exportWorking = d.v === 'working'; render(); },
    copyexport: () => { navigator.clipboard?.writeText(JSON.stringify(state.exportWorking ? wc.working : wc.applied, null, 2)); },
    compare: () => { state[d.side === 'a' ? 'compareA' : 'compareB'] = Number(d.rev); render(); },
  };

  if (actions[act]) { actions[act](); return; }
  if (schemaActions(act, d, el, ev)) return;
  if (ruleActions(act, d, el, ev)) return;
  if (accessActions(act, d, el, ev)) return;
  if (dataActions(act, d, el, ev)) return;
  if (applyActions(act, d, el, ev)) return;
});

/* --- Schema-side actions -------------------------------------------------- */

/** Patches whichever field the editor is showing: the draft, or one in the working copy. */
function patchField(entityName, fieldName, patch) {
  if (state.selectedField === '__new') {
    state.draftField = { ...state.draftField, ...patch };
    for (const [key, value] of Object.entries(patch)) {
      if (value === undefined || value === null || value === '' || value === false) delete state.draftField[key];
    }
    return;
  }
  editors.setField(entityName, fieldName, patch);
}

/** The field the editor is showing, from whichever place holds it. */
function editingField(e) {
  return state.selectedField === '__new' ? (state.draftField ?? {}) : e.fields.find((f) => f.name === state.selectedField) ?? {};
}

function schemaActions(act, d, el, ev) {
  const e = entityView(state.entity) ?? entities()[0];
  switch (act) {
    case 'settype': {
      if (state.selectedField === '__new') {
        const { name } = state.draftField ?? {};
        state.draftField = { name, type: d.type, ...defaultsForType(d.type) };
      } else {
        const current = e.fields.find((f) => f.name === state.selectedField);
        editors.replaceField(e.name, state.selectedField, { ...stripFacets(current), type: d.type, ...defaultsForType(d.type) });
      }
      render();
      return true;
    }
    case 'setfacet': {
      const value = d.value !== undefined ? d.value : el.value;
      const parsed = d.kind === 'int' ? (value === '' ? undefined : Number(value)) : (value === '' ? undefined : value);
      rememberCaret(el);
      patchField(e.name, d.field, { [d.key]: parsed });
      render();
      return true;
    }
    case 'toggleflag': {
      const f = editingField(e);
      patchField(e.name, d.field, { [d.key]: !f?.[d.key] });
      render();
      return true;
    }
    case 'enumadd': return true;
    case 'enumremove': {
      const f = editingField(e);
      patchField(e.name, d.field, { values: (f.values ?? []).filter((v) => v !== d.value) });
      render();
      return true;
    }
    case 'removefield': {
      editors.removeField(e.name, d.field);
      state.selectedField = null;
      render();
      return true;
    }
    case 'addfield': {
      const draft = state.draftField ?? {};
      const name = (draft.name ?? '').trim();
      if (!/^[a-z][a-z0-9_]{0,62}$/.test(name)) {
        state.fieldError = `A field name matches ${SCHEMA_FACETS.entityNamePattern} — lower case, starting with a letter.`;
        render();
        return true;
      }
      const missing = missingFacets(draft);
      if (missing.length) {
        state.fieldError = `The schema requires ${missing.join(' and ')} on ${/^[aeiou]/.test(draft.type) ? 'an' : 'a'} ${draft.type}. Without ${missing.length > 1 ? 'them' : 'it'} the apply refuses the whole descriptor, so the field is not added.`;
        render();
        return true;
      }
      const { name: _drop, ...body } = draft;
      editors.replaceField(e.name, name, body);
      state.selectedField = name;
      state.draftField = null;
      state.fieldError = null;
      render();
      return true;
    }
    case 'derive': {
      /* A rollup aggregates over a child entity that points HERE, so the default is one that
         actually does — a rollup naming an entity with no ref back is refused at apply. */
      const child = entities().find((x) => x.fields.some((f) => f.type === 'ref' && f.entity === e.name));
      const patch = d.kind === 'computed'
        ? { computed: `${e.fields.find((f) => ['integer', 'decimal'].includes(f.type))?.name ?? 'id'} * 1` }
        : { rollup: { from: child?.name ?? entities().find((x) => x.name !== e.name)?.name ?? e.name, op: 'count' } };
      patchField(e.name, d.field, { ...patch, required: undefined, unique: undefined, index: undefined });
      render();
      return true;
    }
    case 'underive': {
      if (state.selectedField === '__new') {
        const { computed, rollup, ...rest } = state.draftField ?? {};
        state.draftField = rest;
      } else {
        const { computed, rollup, ...rest } = wc.working.entities[e.name].fields[d.field];
        editors.replaceField(e.name, d.field, rest);
      }
      render();
      return true;
    }
    case 'rollupfrom': {
      const f = editingField(e);
      patchField(e.name, d.field, { rollup: { ...f.rollup, from: d.value } });
      render();
      return true;
    }
    case 'rollupop': {
      const f = editingField(e);
      const next = { ...f.rollup, op: d.value };
      if (d.value === 'count') delete next.field;
      else next.field ??= entityView(next.from)?.fields.find((x) => ['integer', 'decimal'].includes(x.type))?.name;
      patchField(e.name, d.field, { rollup: next });
      render();
      return true;
    }
    case 'removeindex': {
      editors.setIndexes(e.name, e.indexes.filter((_, i) => i !== Number(d.i)));
      render();
      return true;
    }
    case 'indexpick': {
      const list = state.indexDraft ?? [];
      state.indexDraft = list.includes(d.field) ? list.filter((x) => x !== d.field) : [...list, d.field];
      render();
      return true;
    }
    case 'addindex': {
      const target = entityView(d.entity);
      editors.setIndexes(d.entity, [...target.indexes, { fields: state.indexDraft }]);
      state.indexDraft = null;
      state.overlay = null;
      render();
      return true;
    }
    case 'netenancy': { state.newEntityTenancy = d.value; render(); return true; }
    case 'neaudit': { state.newEntityAudit = !state.newEntityAudit; render(); return true; }
    case 'addentity': {
      const name = ($('#ne-name')?.value ?? '').trim();
      if (!/^[a-z][a-z0-9_]{0,62}$/.test(name)) {
        state.newEntityName = name;
        state.newEntityError = 'An entity name is lower case, starts with a letter, and holds letters, digits and underscores.';
        render();
        return true;
      }
      editors.addEntity(name, {
        description: '',
        ...(tenancyEnabled() ? { tenancy: state.newEntityTenancy ?? 'scoped' } : {}),
        ...(state.newEntityAudit ? { audit: true } : {}),
        fields: { name: { type: 'string', required: true, maxLength: 120, description: '' } },
      });
      state.newEntityName = null;
      state.newEntityAudit = false;
      state.overlay = null;
      location.hash = `#/schema/${name}`;
      return true;
    }
    case 'removehook': {
      const hooks = { ...(e.hooks ?? {}) };
      hooks[d.point] = (hooks[d.point] ?? []).filter((_, i) => i !== Number(d.i));
      if (!hooks[d.point].length) delete hooks[d.point];
      editors.setHooks(e.name, hooks);
      render();
      return true;
    }
    case 'hookpoint': { state.hookDraft = { ...(state.hookDraft ?? { type: 'reject' }), point: d.value }; render(); return true; }
    case 'hooktype': { state.hookDraft = { ...(state.hookDraft ?? { point: 'beforeCreate' }), type: d.value }; render(); return true; }
    case 'addhook': {
      const draft = state.hookDraft ?? { point: 'beforeCreate', type: 'reject' };
      const arg = $('#hook-arg')?.value ?? '';
      const when = $('#hook-when')?.value ?? '';
      const target = entityView(d.entity);
      const body = { type: draft.type };
      if (when) body.when = when;
      if (draft.type === 'reject') body.message = arg;
      if (draft.type === 'mutate') { body.field = arg; body.value = ''; }
      if (draft.type === 'email') body.template = arg;
      if (draft.type === 'webhook') body.endpoint = arg;
      const hooks = { ...(target.hooks ?? {}) };
      hooks[draft.point] = [...(hooks[draft.point] ?? []), body];
      editors.setHooks(d.entity, hooks);
      state.hookDraft = null;
      state.overlay = null;
      render();
      return true;
    }
    case 'addendpoint': {
      const list = wc.working.webhooks?.endpoints ?? [];
      const name = ($('#ep-name')?.value ?? '').trim();
      if (!name) return true;
      editors.setAt('/webhooks', { endpoints: [...list, { name, url: $('#ep-url')?.value ?? '', secretRef: $('#ep-secret')?.value || undefined }] });
      state.overlay = null;
      render();
      return true;
    }
    case 'addtemplate': {
      const name = ($('#tp-name')?.value ?? '').trim();
      if (!name) return true;
      editors.setAt('/templates', { ...(wc.working.templates ?? {}), [name]: { subject: $('#tp-subject')?.value ?? '', body: $('#tp-body')?.value ?? '' } });
      state.overlay = null;
      render();
      return true;
    }
    case 'entityfind': return true;
    default: return false;
  }
}

const FACET_KEYS = ['maxLength', 'format', 'precision', 'scale', 'values', 'entity', 'onDelete'];
const stripFacets = (f) => Object.fromEntries(Object.entries(f ?? {}).filter(([k]) => !FACET_KEYS.includes(k) && k !== 'name'));

function defaultsForType(type) {
  /* `decimal` and `ref` get a default the author can accept or change; `enum` gets NONE, because
     two invented values are content nobody wrote — and an empty required block is exactly what
     "a block the form will not let you leave empty" is supposed to look like. */
  if (type === 'decimal') return { precision: 10, scale: 2 };
  if (type === 'ref') return { entity: entities()[0]?.name, onDelete: 'restrict' };
  return {};
}

/** What the schema requires of a field of this type, and whether this one has it. */
function missingFacets(field) {
  return (SCHEMA_FACETS.needs[field.type] ?? []).filter((k) => {
    const v = field[k];
    return v === undefined || v === null || v === '' || (Array.isArray(v) && v.length === 0);
  });
}

/* --- Rule actions --------------------------------------------------------- */

function ruleActions(act, d, el, ev) {
  if (!['ruleopen', 'rulerole', 'ruleowner', 'ruleraw', 'condadd', 'condremove'].includes(act)) return false;
  const e = entityView(d.entity ?? state.entity) ?? entities()[0];

  if (act === 'ruleopen') {
    state.ruleOpen = state.ruleOpen === d.op ? null : d.op;
    render();
    if (state.ruleOpen) $(`#rule-${d.op}`)?.scrollIntoView({ block: 'center', behavior: 'smooth' });
    return true;
  }

  const m = ruleModel(e, d.op);

  if (act === 'rulerole' || act === 'ruleowner') {
    const who = act === 'rulerole' ? { kind: 'role', role: d.role } : { kind: 'owner', field: d.field };
    m.branches = m.branches || [];
    const b = branchFor(m, who);
    if (b) m.branches.splice(m.branches.indexOf(b), 1);
    else m.branches.push({ ...who, conds: [] });
    writeRule(e, d.op, m);
    /* Opening the editor for the operation just changed, so "+ add" does not act 700 px away. */
    state.ruleOpen = d.op;
    render();
    $(`#rule-${d.op}`)?.scrollIntoView({ block: 'center', behavior: 'smooth' });
    return true;
  }

  if (act === 'ruleraw') {
    state.rawOps ??= new Set();
    const key = `${e.name}:${d.op}`;
    if (state.rawOps.has(key)) state.rawOps.delete(key);
    else state.rawOps.add(key);
    render();
    return true;
  }

  if (act === 'condadd') {
    const options = testable(e);
    const f = options.find((x) => x.type === 'enum') || options.find((x) => x.type === 'boolean') || options[0];
    if (!f) return true;
    const b = m.branches[Number(d.b)];
    b.conds = [...(b.conds || []), newCond(e, f)];
    writeRule(e, d.op, m);
    render();
    return true;
  }

  if (act === 'condremove') {
    m.branches[Number(d.b)].conds.splice(Number(d.i), 1);
    writeRule(e, d.op, m);
    render();
    return true;
  }
  return false;
}

/* --- Access actions ------------------------------------------------------- */

function accessActions(act, d, el, ev) {
  switch (act) {
    case 'unassign': {
      const m = membershipOf(d.id);
      state.membership[d.id] = { ...m, roleNames: m.roleNames.filter((r) => r !== d.role) };
      state.membershipLog.push(`removed ${d.role} from ${userById(d.id).email}`);
      render();
      return true;
    }
    case 'assign': {
      if (d.role === 'admin' && !d.force) { state.pendingGrant = 'admin'; render(); return true; }
      const m = membershipOf(d.id);
      if (!m.roleNames.includes(d.role)) {
        /* Nobody raises their own level. The server enforces it; this only refuses to ask. */
        if (d.id === state.signedIn) { state.overlay = null; render(); return true; }
        state.membership[d.id] = { ...m, roleNames: [...m.roleNames, d.role] };
        state.membershipLog.push(`gave ${d.role} to ${userById(d.id).email}`);
      }
      state.pendingGrant = null;
      state.overlay = null;
      render();
      return true;
    }
    case 'settenantvalue': { const input = $('#tenant-value'); if (input) input.value = d.value; return true; }
    case 'settenant': {
      const value = ($('#tenant-value')?.value ?? '').trim();
      const m = membershipOf(d.id);
      state.membership[d.id] = { ...m, tenant: value || null };
      state.membershipLog.push(value ? `${userById(d.id).email} now acts in ${shortTenant(value)}` : `${userById(d.id).email} carries no tenant`);
      state.overlay = null;
      render();
      return true;
    }
    case 'createperson': {
      const email = ($('#np-email')?.value ?? '').trim();
      if (!email.includes('@')) return true;
      const id = crypto.randomUUID ? crypto.randomUUID() : `${Date.now()}`;
      USERS.push({ id, email, roleNames: [], isDisabled: false, tenant: null, bootstrap: false, self: false });
      state.membership[id] = { roleNames: [], tenant: null, isDisabled: false };
      state.credentialToken = `set-password:${(crypto.randomUUID ? crypto.randomUUID() : 'token').replace(/-/g, '').slice(0, 24)}`;
      state.membershipLog.push(`created ${email}`);
      render();
      return true;
    }
    case 'addrole': {
      const name = ($('#nr-name')?.value ?? '').trim();
      if (!/^[a-z][a-z0-9_-]*$/.test(name)) { state.newRoleName = name; render(); return true; }
      editors.setRoles([...declaredRoles(), name]);
      state.newRoleName = null;
      state.overlay = null;
      render();
      return true;
    }
    case 'declarerole': {
      editors.setRoles([...declaredRoles(), d.role]);
      render();
      return true;
    }
    case 'delrole': {
      editors.setRoles(declaredRoles().filter((r) => r !== d.role));
      render();
      return true;
    }
    case 'setaccess': {
      const value = d.value !== undefined ? d.value : el.value;
      rememberCaret(el);
      editors.setAccess(d.level, value);
      render();
      return true;
    }
    default: return false;
  }
}

/* --- Data actions --------------------------------------------------------- */

function dataActions(act, d, el, ev) {
  switch (act) {
    case 'pick': {
      if (state.selectedRows.has(d.id)) state.selectedRows.delete(d.id);
      else state.selectedRows.add(d.id);
      render();
      return true;
    }
    case 'pickopen': { state.pickerOpen = state.pickerOpen === d.field ? null : d.field; state.pickQuery = ''; render(); return true; }
    case 'pick-ref': {
      state.form.values = { ...state.form.values, [d.field]: d.id };
      state.pickerOpen = null;
      render();
      return true;
    }
    case 'pickquery': return true;
    case 'formpick': { state.form.values = { ...state.form.values, [d.field]: d.value }; render(); return true; }
    case 'formtoggle': { state.form.values = { ...state.form.values, [d.field]: !state.form.values[d.field] }; render(); return true; }
    case 'togglecolumn': {
      const e = entityView(d.entity);
      const current = columnsFor(e).map((f) => f.name);
      const next = current.includes(d.field) ? current.filter((x) => x !== d.field) : [...current, d.field];
      state.columns = { ...(state.columns ?? {}), [d.entity]: next.length ? next : current };
      render();
      return true;
    }
    case 'submitrecord': {
      submitRecord(d.entity, d.edit);
      return true;
    }
    default: return false;
  }
}

/* The validation the API would do, with the API's own refusals. Unique is the interesting one:
   it is `409 conflict` with a per-violation code, never a slug of its own. */
function submitRecord(entityName, editId) {
  const e = entityView(entityName);
  const values = state.form.values;
  const errors = [];

  for (const f of e.fields) {
    if (f.readOnly === true || f.computed || f.rollup) continue;
    const v = values[f.name];
    const empty = v === undefined || v === null || v === '';
    if (f.required && empty && !editId) {
      errors.push({ field: f.name, slug: 'validation', code: 'required', title: `${f.name} is required`, detail: `The descriptor marks ${f.name} required, so a create without it is refused before it reaches the database.`, fix: f.hidden === true ? 'It is hidden as well as required — which is exactly why it is on this form at all.' : 'Give it a value.' });
      continue;
    }
    if (empty) continue;
    if (f.maxLength && String(v).length > f.maxLength) {
      errors.push({ field: f.name, slug: 'validation', code: 'max-length', title: `${f.name} is too long`, detail: `The column holds ${f.maxLength} characters and this is ${String(v).length}.`, fix: `Shorten it, or widen maxLength in Schema — widening is safe, narrowing is not.` });
    }
    if (f.format && !matchesFormat(f.format, String(v))) {
      const pattern = wc.working.formats?.[f.format]?.pattern;
      errors.push({ field: f.name, slug: 'validation', code: 'format', title: `${f.name} does not match ${f.format}`, detail: pattern ? `The format is /${pattern}/, and Alvo anchors it over the whole value — a value with trailing text is refused by the framework's anchoring rather than by the author's regex.` : `A built-in format.`, fix: 'Correct the value.' });
    }
    if (f.unique && (ROWS[e.name] ?? []).some((r) => r.id !== editId && String(r[f.name]) === String(v))) {
      errors.push({ field: f.name, slug: 'conflict', code: 'unique', title: `${f.name} is already taken`, detail: `Another record holds ${v}, and ${f.name} is unique. The database refused the write — this is 409, not a validation failure: the request is well formed and collides with what is stored.`, fix: 'Use a different value. There is no unique-violation slug: one `conflict` covers a unique collision and a restricted reference, and the violation code tells them apart.' });
    }
  }

  state.form.errors = errors;
  if (errors.length) { render(); return; }

  const row = { id: `new_${Date.now().toString(36)}`, tenant: myTenant(), version: 1 };
  for (const f of e.fields) if (values[f.name] !== undefined) row[f.name] = values[f.name];
  if (editId) {
    const existing = (ROWS[e.name] ?? []).find((r) => r.id === editId);
    Object.assign(existing, row, { id: editId, version: (existing.version ?? 1) + 1 });
  } else {
    (ROWS[e.name] ??= []).unshift(row);
    ROW_COUNTS[e.name] = (ROW_COUNTS[e.name] ?? 0) + 1;
  }
  state.form = { errors: [], values: {} };
  state.overlay = null;
  render();
}

function matchesFormat(name, value) {
  const builtIn = { email: /^[^@\s]+@[^@\s]+\.[^@\s]+$/, uri: /^[a-z][a-z0-9+.-]*:\/\/\S+$/i, phone: /^[+0-9 ()-]{5,}$/ };
  if (builtIn[name]) return builtIn[name].test(value);
  const pattern = wc.working.formats?.[name]?.pattern;
  if (!pattern) return true;
  return new RegExp(`^(?:${pattern})$`).test(value);   // Alvo anchors a format over the whole value
}

/* --- Apply, rollback, import --------------------------------------------- */

function applyActions(act, d, el, ev) {
  switch (act) {
    case 'confirmword': return true;
    case 'applyreason': return true;
    case 'apply': {
      const migration = workingPlan();
      const typed = $('[data-act="confirmword"]')?.value?.trim();
      const word = $('[data-act="confirmword"]')?.dataset.word;
      const allowDestructive = !migration.hasDestructiveChanges || typed === word;
      if (migration.hasDestructiveChanges && !allowDestructive) {
        state.applyState = 'refused-destructive';
        render();
        return true;
      }
      const result = applyWorking({
        author: me().email,
        reason: $('#apply-reason')?.value || '',
        allowDestructive,
      });
      if (!result.applied) { state.applyState = 'refused-destructive'; render(); return true; }
      state.applyState = null;
      state.applyReason = '';
      state.rawOps?.clear();
      location.hash = '#/history';
      return true;
    }
    case 'dorollback': {
      const typed = $('[data-act="confirmword"]')?.value?.trim();
      const word = $('[data-act="confirmword"]')?.dataset.word;
      const result = restoreRevision(Number(d.id), { author: me().email, allowDestructive: !word || typed === word });
      if (!result.applied) { render(); return true; }
      state.overlay = null;
      state.compareA = Number(d.id);
      state.compareB = wc.revision;
      render();
      return true;
    }
    case 'import': {
      const text = $('#import-json')?.value ?? '';
      try {
        const parsed = JSON.parse(text);
        if (!parsed.apiVersion || !parsed.name || !parsed.entities) throw new Error('A descriptor needs apiVersion, name and entities.');
        wc.working = parsed;
        state.importError = null;
        location.hash = '#/schema/preview';
      } catch (error) {
        state.importError = { title: 'That is not a descriptor this API can read', detail: error.message };
        render();
      }
      return true;
    }
    case 'finishwizard': {
      startEmpty((state.wizardName ?? 'my-project').trim());
      wc.working.description = state.wizardDesc ?? '';
      if (state.wizardTenancy) wc.working.tenancy = { enabled: true };
      state.membership[state.signedIn] = { ...membershipOf(state.signedIn), tenant: state.wizardTenancy ? (state.wizardTenant ?? TENANTS[0].id) : null };
      applyWorking({ author: me().email, reason: 'First apply' });
      location.hash = '#/schema';
      return true;
    }
    case 'wizardname': case 'wizarddesc': case 'wizardtenant': return true;
    case 'wizardtenancy': { state.wizardTenancy = !state.wizardTenancy; render(); return true; }
    default: return false;
  }
}

/* --- Typing --------------------------------------------------------------- */

document.addEventListener('input', (ev) => {
  const el = ev.target.closest('[data-act]');
  if (!el) return;
  const act = el.dataset.act;
  const d = el.dataset;

  if (act === 'fieldfilter') { state.filterFields = el.value; rememberCaret(el); render(); return; }
  if (act === 'pickquery') { state.pickQuery = el.value; rememberCaret(el); render(); return; }
  if (act === 'applyreason') { state.applyReason = el.value; return; }
  if (act === 'wizardname') { state.wizardName = el.value; return; }
  if (act === 'wizarddesc') { state.wizardDesc = el.value; return; }
  if (act === 'wizardtenant') { state.wizardTenant = el.value; return; }
  if (act === 'confirmword') {
    const ok = el.value.trim() === el.dataset.word;
    for (const button of $$('[data-needs-confirm]')) {
      button.disabled = !ok;
      button.setAttribute('aria-disabled', String(!ok));
    }
    return;
  }
  if (act === 'entityfind') {
    if (entities().some((e) => e.name === el.value)) location.hash = `${state.route.startsWith('#/data') ? '#/data' : state.route.startsWith('#/rules') ? '#/rules' : '#/schema'}/${el.value}`;
    return;
  }
  if (act === 'setname') {
    if (state.selectedField === '__new') { state.draftField = { ...state.draftField, name: el.value }; return; }
    return;
  }
  if (act === 'formfield') {
    const e = entityView(state.overlay?.id ?? state.entity);
    const f = e?.fields.find((x) => x.name === d.field);
    let v = el.value;
    if (f?.type === 'integer') v = v === '' ? '' : Number(v);
    if (f?.type === 'decimal') v = v === '' ? '' : Number(v);
    state.form.values = { ...state.form.values, [d.field]: v };
    return;
  }
  if (act === 'setfacet') {
    const e = entityView(state.entity);
    const parsed = d.kind === 'int' ? (el.value === '' ? undefined : Number(el.value)) : (el.value === '' ? undefined : el.value);
    patchField(e.name, d.field, { [d.key]: parsed });
    softRender();
    return;
  }
  if (act === 'setaccess') {
    editors.setAccess(d.level, el.value);
    softRender();
    return;
  }
  if (act === 'rawcel') {
    const e = entityView(d.entity);
    editors.setRule(e.name, d.op, el.value);
    softRender();
    return;
  }
  if (act === 'enumadd') {
    if (!el.value.endsWith(',') && !el.value.endsWith(' ')) return;
    const e = entityView(state.entity);
    const f = editingField(e);
    const value = el.value.trim().replace(/,$/, '');
    if (value && !(f.values ?? []).includes(value)) patchField(e.name, d.field, { values: [...(f.values ?? []), value] });
    render();
    return;
  }
});

document.addEventListener('change', (ev) => {
  const el = ev.target.closest('[data-act]');
  if (!el) return;
  const act = el.dataset.act;
  const d = el.dataset;

  if (['condfield', 'condop', 'condvalue'].includes(act)) {
    const e = entityView(d.entity);
    const m = ruleModel(e, d.op);
    const b = m.branches[Number(d.b)];
    const c = b.conds[Number(d.i)];
    if (act === 'condfield') Object.assign(c, newCond(e, e.fields.find((x) => x.name === el.value)));
    else if (act === 'condop') { c.op = el.value; if (NO_VALUE.includes(c.op)) c.value = ''; }
    else c.value = el.value;
    writeRule(e, d.op, m);
    render();
    return;
  }
  if (act === 'setname' && state.selectedField !== '__new') {
    const e = entityView(state.entity);
    const to = el.value.trim();
    if (to && to !== d.field) {
      editors.setField(e.name, d.field, { renamedFrom: d.field });
      editors.renameField(e.name, d.field, to);
      state.selectedField = to;
    }
    render();
  }
});

/* ==========================================================================
   Keyboard

   §5.5 asks for the palette, j/k through rows, / to focus the filter, Enter to open a detail,
   Esc to dismiss anything, and g+letter to jump. The drawn version had ⌘K and Esc and nothing
   else — and the palette itself was keyboard-dead, because `autofocus` does not fire on an
   element inserted through innerHTML.
   ========================================================================== */

const typing = () => ['INPUT', 'TEXTAREA', 'SELECT'].includes(document.activeElement?.tagName);

let gPending = false;

document.addEventListener('keydown', (ev) => {
  /* The palette is modal and owns every key while it is open. */
  if (state.overlay?.kind === 'palette') {
    if (ev.key === 'ArrowDown' || ev.key === 'ArrowUp') {
      ev.preventDefault();
      state.paletteIndex = Math.max(0, state.paletteIndex + (ev.key === 'ArrowDown' ? 1 : -1));
      render();
      return;
    }
    if (ev.key === 'Enter') {
      ev.preventDefault();
      const active = $('.a-palette__item--active');
      if (active?.dataset.route) { state.overlay = null; location.hash = active.dataset.route; }
      return;
    }
  }

  if ((ev.metaKey || ev.ctrlKey) && ev.key.toLowerCase() === 'k') {
    ev.preventDefault();
    if (state.overlay?.kind === 'palette') { state.overlay = null; }
    else { state.paletteQuery = ''; state.paletteIndex = 0; openOverlay('palette'); }
    render();
    return;
  }

  if (ev.key === 'Escape') {
    if (state.overlay) { state.overlay = null; state.pendingGrant = null; render(); return; }
    if (state.selectedField !== null) { state.selectedField = null; render(); return; }
    return;
  }

  /* A focus trap inside any open overlay: Tab must not walk the page behind it. */
  if (ev.key === 'Tab' && state.overlay) {
    const panel = $('.p-overlay__panel');
    if (!panel) return;
    const items = $$(FOCUSABLE, panel).filter((el) => el.offsetParent !== null);
    if (!items.length) return;
    const first = items[0];
    const last = items[items.length - 1];
    if (!ev.shiftKey && document.activeElement === last) { ev.preventDefault(); first.focus(); }
    else if (ev.shiftKey && document.activeElement === first) { ev.preventDefault(); last.focus(); }
    return;
  }

  if (typing()) return;

  if (gPending) {
    gPending = false;
    const target = GOTO[ev.key.toLowerCase()];
    if (target) { ev.preventDefault(); location.hash = target; }
    return;
  }
  if (ev.key === 'g') { gPending = true; setTimeout(() => { gPending = false; }, 1200); return; }

  if (ev.key === '/') {
    const filter = $('[data-act="fieldfilter"]') ?? $('[aria-label="Search"]') ?? $('.a-input');
    if (filter) { ev.preventDefault(); filter.focus(); }
    return;
  }

  if (ev.key === 'j' || ev.key === 'k' || ev.key === 'ArrowDown' || ev.key === 'ArrowUp') {
    const rows = $$('tbody tr[tabindex], .a-fieldrow, .a-row-card[tabindex], [data-act="person"], .a-subgrid__row');
    if (!rows.length) return;
    const here = rows.indexOf(document.activeElement);
    const next = ev.key === 'j' || ev.key === 'ArrowDown' ? Math.min(rows.length - 1, here + 1) : Math.max(0, here - 1);
    ev.preventDefault();
    rows[next]?.focus();
    return;
  }

  if (ev.key === 'Enter' || ev.key === ' ') {
    const el = document.activeElement;
    if (!el) return;
    if (el.getAttribute('role') === 'switch' || el.getAttribute('role') === 'checkbox' || (el.dataset?.act && el.tagName !== 'BUTTON' && el.tagName !== 'A')) {
      ev.preventDefault();
      el.click();
    }
  }
});

window.addEventListener('hashchange', () => {
  const next = location.hash || '#/overview';
  if (!next.startsWith('#/schema/')) { state.tab = 'fields'; state.selectedField = null; }
  state.route = next;
  state.overlay = null;
  state.ruleOpen = null;
  state.form = { errors: [], values: {} };
  state.pickerOpen = null;
  render();
});

/* ==========================================================================
   Prototype chrome
   ========================================================================== */

const systemDark = window.matchMedia('(prefers-color-scheme: dark)').matches;
document.documentElement.setAttribute('data-theme', systemDark ? 'dark' : 'light');

const themeButton = $('#theme');
const densityButton = $('#density');
if (themeButton) {
  themeButton.textContent = systemDark ? 'Dark' : 'Light';
  themeButton.addEventListener('click', () => {
    const next = document.documentElement.getAttribute('data-theme') === 'dark' ? 'light' : 'dark';
    document.documentElement.setAttribute('data-theme', next);
    themeButton.textContent = next === 'dark' ? 'Dark' : 'Light';
  });
}
if (densityButton) {
  densityButton.addEventListener('click', () => {
    const next = document.documentElement.getAttribute('data-density') === 'comfortable' ? 'compact' : 'comfortable';
    document.documentElement.setAttribute('data-density', next);
    densityButton.textContent = next === 'comfortable' ? 'Comfortable' : 'Compact';
  });
}

/* A hook the scenario suite uses to start from a known state, and nothing else does. */
window.__alvoPrototype = {
  reset() { resetWorking(); Object.assign(state, { overlay: null, selectedField: null, ruleOpen: null, rawOps: new Set(), columns: null, form: { errors: [], values: {} }, membershipLog: [] }); render(); },
  startEmpty(name) { startEmpty(name); render(); },
  state, wc, count, changes,
};

render();
