import {
  REFUSED, WARNED, TYPES, FACETS, PROJECT, TENANTS, ENTITIES, WORK_ORDERS, CUSTOMERS,
  REGIONS, CALLERS, REVISIONS, USERS, BUILTIN_ROLES, ACCESS_LEVELS, API_KEYS, ENDPOINTS, TEMPLATES, AI_TOOLS, PALETTE_ITEMS,
} from './data.js';

/* ==========================================================================
   State
   ========================================================================== */

const state = {
  route: location.hash || '#/overview',
  overlay: null,
  ai: false,
  selectedField: null,
  entity: 'work_orders',
  tab: 'fields',
  tenant: TENANTS[0].id,
  selectedRows: new Set(),
  pending: 2,
  screenState: 'ready',
  ruleOpen: null,
  rules: {},   // per entity, parsed from the descriptor on first read
  simulate: { role: 'technician', record: 'wo_08fa2' },
  compareB: 6,
  roleCatalog: null,      // the descriptor's auth.roles, as edited
  assigned: null,         // email -> roles, as edited (identity, immediate)
  person: null,           // whose access ladder is open
  pickerChosen: null,
};

const $ = (sel, root = document) => root.querySelector(sel);
const esc = (s) => String(s).replace(/[&<>"]/g, (c) => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;' }[c]));
const entity = (name) => ENTITIES.find((e) => e.name === name);
const eur = (n) => n.toLocaleString('en-US', { style: 'currency', currency: 'EUR' });

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
    spark: '<path d="M10 3l1.6 4.4L16 9l-4.4 1.6L10 15l-1.6-4.4L4 9l4.4-1.6z"/>',
  }[name] || '';
  return `<svg viewBox="0 0 20 20" width="16" height="16" fill="none" stroke="currentColor" stroke-width="1.6" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true">${p}</svg>`;
}

const mark = (size = 26) => `<img src="alvo-mark.svg" width="${size}" height="${size}" alt="" style="flex:none">`;
const avatar = (ch) => `<span class="a-brand-mark" style="background:var(--panel2);color:var(--dim)">${esc(ch)}</span>`;

/* ==========================================================================
   Field helpers
   ========================================================================== */

function flagBadges(f) {
  const out = [];
  if (f.computed) out.push('<span class="a-badge a-badge--accent">computed</span>');
  if (f.rollup) out.push(`<span class="a-badge a-badge--accent">${f.rollup.op} of ${f.rollup.from}</span>`);
  if (f.required) out.push('<span class="a-badge">required</span>');
  if (f.unique) out.push('<span class="a-badge">unique</span>');
  if (f.hidden) out.push('<span class="a-badge a-badge--warn">hidden</span>');
  if (f.readOnly) out.push('<span class="a-badge">read only</span>');
  if (f.format) out.push(`<span class="a-badge">${esc(f.format)}</span>`);
  if (f.maxLength) out.push(`<span class="a-badge">max ${f.maxLength}</span>`);
  if (f.precision) out.push(`<span class="a-badge">${f.precision},${f.scale}</span>`);
  if (f.values) out.push(`<span class="a-badge">${f.values.length} values</span>`);
  if (f.onDelete) out.push(`<span class="a-badge">on delete ${esc(f.onDelete)}</span>`);
  return out.join(' ');
}

const typeLabel = (f) => (f.type === 'ref' ? `ref → ${f.entity}` : f.type);

function fieldObject(f) {
  const o = { type: f.type };
  if (f.required) o.required = true;
  if (f.unique) o.unique = true;
  if (f.readOnly) o.readOnly = true;
  if (f.hidden) o.hidden = true;
  if (f.maxLength) o.maxLength = f.maxLength;
  if (f.precision) { o.precision = f.precision; o.scale = f.scale; }
  if (f.values) o.values = f.values;
  if (f.format) o.format = f.format;
  if (f.computed) o.computed = f.computed;
  if (f.rollup) o.rollup = f.rollup;
  if (f.entity) { o.entity = f.entity; o.onDelete = f.onDelete; }
  return o;
}

const fieldJson = (f) => JSON.stringify({ [f.name]: fieldObject(f) }, null, 2);

function highlight(json) {
  return esc(json)
    .replace(/&quot;([^&]+?)&quot;(\s*:)/g, '<span class="a-code-key">"$1"</span>$2')
    .replace(/(:\s)&quot;([^&]*?)&quot;/g, '$1<span class="a-code-str">"$2"</span>');
}

/* The descriptor pane. Each field's lines carry its name so selecting the
   field on the left can mark and scroll to them on the right. */
function descriptorJson(ent, selected) {
  const lines = [];
  const push = (t, mk) => lines.push({ t, mk });

  push('{');
  push(`  "${ent.name}": {`);
  push(`    "tenancy": "${ent.tenancy}",`);
  if (ent.audit) push('    "audit": true,');
  push('    "fields": {');
  ent.fields.forEach((f, i) => {
    const body = JSON.stringify({ [f.name]: fieldObject(f) }, null, 2).split('\n').slice(1, -1);
    body.forEach((l, j) => {
      const last = j === body.length - 1 && i !== ent.fields.length - 1;
      push('    ' + l + (last ? ',' : ''), f.name);
    });
  });
  push('    },');
  push('    "rules": {');
  const liveRules = Object.fromEntries(Object.entries(rulesFor(ent.name)).map(([k, m]) => [k, celOf(m, ent)]));
  Object.entries(liveRules).filter(([, v]) => v).forEach(([k, v], i, a) => push(`      "${k}": "${v}"${i === a.length - 1 ? '' : ','}`));
  push(`    }${ent.indexes.length ? ',' : ''}`);
  if (ent.indexes.length) {
    push('    "indexes": [');
    ent.indexes.forEach((ix, i, a) => push(`      { "fields": [${ix.fields.map((x) => `"${x}"`).join(', ')}] }${i === a.length - 1 ? '' : ','}`));
    push('    ]');
  }
  push('  }');
  push('}');

  return lines.map(({ t, mk }) => {
    const html = highlight(t);
    return mk && mk === selected ? `<span class="a-json__hit" id="json-${mk}">${html}</span>` : html;
  }).join('\n');
}

/* ==========================================================================
   Shell
   ========================================================================== */

const NAV = [
  { key: 'overview', label: 'Overview', icon: 'grid', route: '#/overview' },
  { key: 'schema', label: 'Schema', icon: 'schema', route: '#/schema' },
  { key: 'data', label: 'Data', icon: 'rows', route: '#/data' },
  { key: 'rules', label: 'Rules', icon: 'rule', route: '#/rules' },
  { key: 'access', label: 'Access', icon: 'shield', route: '#/access' },
  { key: 'integrations', label: 'Integrations', icon: 'plug', route: '#/integrations' },
  { key: 'history', label: 'Configuration history', icon: 'clock', route: '#/history' },
  { sep: true },
  { key: 'automations', label: 'Automations', icon: 'bolt', route: '#/automations', notYet: true },
  { key: 'functions', label: 'Functions', icon: 'fn', route: '#/functions', notYet: true },
  { sep: true },
  { key: 'settings', label: 'Settings', icon: 'cog', route: '#/settings' },
];

const activeKey = () => (state.route.split('/')[1] || 'overview');

function sidebar() {
  const items = NAV.map((n) => {
    if (n.sep) return '<div class="a-nav-sep"></div>';
    const on = activeKey() === n.key ? ' a-nav-item--active' : '';
    const trail = n.notYet ? '<span class="a-notyet a-nav-item__trail">Not yet</span>' : '';
    return `<a class="a-nav-item${on}" href="${n.route}">${icon(n.icon)}<span>${n.label}</span>${trail}</a>`;
  }).join('');

  return `<aside class="a-sidebar">
    <div class="a-brand">${mark(28)} Alvo</div>
    <button class="a-switcher" data-act="overlay" data-kind="projects">
      ${avatar('F')}
      <span><span class="a-switcher-name">field-service</span>
      <span class="a-switcher-meta">revision 7 · PostgreSQL</span></span>
    </button>
    <nav class="a-nav">${items}</nav>
    <div style="margin-top:auto;display:flex;flex-direction:column;gap:var(--space-3)">
      <button class="a-btn a-btn--primary" data-act="ai">${icon('spark')} Ask Alvo
        <span class="a-kbd" style="margin-left:auto;background:transparent;border-color:currentColor;color:inherit">⌘K</span></button>
      <div class="a-row">${avatar('JK')}
        <span><span class="a-switcher-name">Jana Kováčová</span>
        <span class="a-switcher-meta">admin · bootstrap</span></span></div>
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

function entityBar(active, base, trailing) {
  return `<div class="a-entitybar">
    ${ENTITIES.map((e) => `<button class="a-entitybar__item${e.name === active ? ' a-entitybar__item--on' : ''}" data-act="go" data-route="${base}/${e.name}">
      ${e.name}<span class="a-entitybar__count">${e.fields.length}</span></button>`).join('')}
    <span class="a-entitybar__add">${trailing}</span>
  </div>`;
}

/* ==========================================================================
   Overview
   ========================================================================== */

function screenOverview() {
  const warned = Object.entries(WARNED).map(([k, v]) => `<div class="a-row" style="align-items:flex-start;padding:var(--space-3) var(--space-5);border-bottom:1px solid var(--border)">
      <code class="a-mono" style="flex:none;width:122px;font-size:var(--text-xs)">${k}</code>
      <span class="p-muted" style="flex:1">${esc(v)}</span>
      <span class="a-notyet">Not yet</span></div>`).join('');

  const cards = [
    ['Entities', ENTITIES.length, 'tables Alvo created and keeps in step'],
    ['Records', '26,532', 'across both tenants'],
    ['Rules', ENTITIES.reduce((n, e) => n + Object.values(e.rules).filter(Boolean).length, 0), 'of 15 operations guarded'],
    ['Hooks', ENTITIES.reduce((n, e) => n + e.hooks.length, 0), 'running on every write'],
  ].map(([k, v, sub]) => `<div class="a-card">
      <span class="a-label">${k}</span>
      <span style="font-size:var(--text-2xl);font-weight:var(--weight-bold);font-variant-numeric:tabular-nums">${v}</span>
      <span class="p-muted">${sub}</span></div>`).join('');

  return `${header([{ label: 'Overview' }], '<button class="a-btn" data-act="go" data-route="#/schema/transfer">Export descriptor</button>')}
  <div class="a-content"><div class="a-stack">
    <div class="p-between">
      <div><h1 class="a-page-title">field-service</h1>
        <p class="p-muted p-tight" style="max-width:64ch">${esc(PROJECT.description)}</p></div>
      <div class="p-hstack">
        <span class="a-badge a-badge--ok"><span class="a-dot"></span> Revision 7 applied</span>
        <span class="a-badge">PostgreSQL 16</span>
        <span class="a-badge">2 tenants</span></div>
    </div>

    <div class="a-cards">${cards}</div>

    ${state.pending ? `<div class="a-row" style="padding:var(--space-4);border:1px solid var(--accentBorder);border-radius:var(--radius-md);background:var(--accentSoft)">
      <span style="font-weight:var(--weight-medium)">Two schema changes are waiting</span>
      <span class="p-muted">Edited and not applied. Nothing has reached the database.</span>
      <button class="a-btn a-btn--sm a-btn--primary" style="margin-left:auto" data-act="go" data-route="#/schema/preview">Review them</button>
    </div>` : ''}

    <div class="a-split">
      <div class="a-stack">
        <div class="a-panel">
          <div class="a-section"><span class="a-section-title">Your entities</span>
            <a class="a-btn a-btn--sm a-btn--ghost" style="margin-left:auto" href="#/schema">Open schema</a></div>
          ${ENTITIES.map((e) => `<div class="a-row" style="align-items:flex-start;padding:var(--space-4) var(--space-5);border-bottom:1px solid var(--border)">
            <span style="flex:1;min-width:0">
              <a style="font-family:var(--font-mono);font-size:var(--text-sm);font-weight:var(--weight-medium)" href="#/schema/${e.name}">${e.name}</a>
              <span class="a-switcher-meta">${e.fields.length} fields · ${e.rows.toLocaleString('en-US')} records · ${e.tenancy}</span></span>
            <span class="p-hstack" style="flex:none">
              <a class="a-btn a-btn--sm a-btn--ghost" href="#/data/${e.name}">Browse</a>
              <a class="a-btn a-btn--sm a-btn--ghost" href="#/schema/${e.name}">Edit</a></span>
          </div>`).join('')}
        </div>

        <div class="a-panel">
          <div class="a-section"><span class="a-section-title">Declared, and not running yet</span>
            <span class="a-section-sub">From <code class="a-mono">GET /management/capabilities</code>. The wording is the server's.</span></div>
          ${warned}
        </div>
      </div>

      <div class="a-stack">
        <div class="a-card">
          <span class="a-row">${icon('spark')}<span class="a-section-title">Ask about this project</span></span>
          <span class="p-muted">It reads your schema, your rules and what this build honours. Changes arrive as a diff you approve — it cannot apply one.</span>
          <div class="a-ai__suggest">
            <button class="a-preset" data-act="ai">Why can a technician not see this job?</button>
            <button class="a-preset" data-act="ai">Add an invoices entity</button></div>
        </div>

        <div class="a-panel">
          <div class="a-section"><span class="a-section-title">Recent changes</span>
            <a class="a-btn a-btn--sm a-btn--ghost" style="margin-left:auto" href="#/history">All</a></div>
          ${REVISIONS.slice(0, 4).map((r) => `<div class="a-row" style="align-items:flex-start;padding:var(--space-3) var(--space-5);border-bottom:1px solid var(--border)">
            <span class="a-badge${r.rolledBackFrom ? ' a-badge--warn' : ''}" style="flex:none">r${r.revision}</span>
            <span style="flex:1;min-width:0"><span style="font-size:var(--text-sm)">${esc(r.reason)}</span>
              <span class="a-switcher-meta">${esc(r.author)} · ${r.at}</span></span></div>`).join('')}
        </div>
      </div>
    </div>
  </div></div>`;
}

/* ==========================================================================
   Schema
   ========================================================================== */

function screenSchemaList() {
  const rows = ENTITIES.map((e) => `<tr data-act="go" data-route="#/schema/${e.name}" style="cursor:pointer">
      <td><span style="font-family:var(--font-mono);font-size:var(--text-sm);font-weight:var(--weight-medium)">${e.name}</span>
        <div class="p-muted" style="max-width:52ch">${esc(e.description.slice(0, 92))}…</div></td>
      <td><span class="a-badge${e.tenancy === 'global' ? '' : ' a-badge--accent'}">${e.tenancy}</span></td>
      <td class="a-num">${e.fields.length}</td>
      <td class="a-num">${e.rows.toLocaleString('en-US')}</td>
      <td>${e.hooks.length ? `<span class="a-badge">${e.hooks.length} hooks</span>` : '<span class="p-muted">—</span>'}</td>
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
      ${ENTITIES.map((e) => `<div class="a-row-card" data-act="go" data-route="#/schema/${e.name}">
        <div class="a-row-card__head"><span style="font-family:var(--font-mono)">${e.name}</span><span class="a-badge">${e.tenancy}</span></div>
        <div class="a-row-card__meta"><span>${e.fields.length} fields</span><span>${e.rows.toLocaleString('en-US')} records</span></div></div>`).join('')}
    </div>

    <div class="a-panel">
      <div class="a-section"><span class="a-section-title">The model</span>
        <span class="a-section-sub">Every relation is many-to-one and starts at the <code class="a-mono">ref</code> field that makes it. The word beside the arrow is what a delete on the other side does. Click a box to open it.</span></div>
      ${entityMap()}
      <div class="p-note" style="margin:0 var(--space-5) var(--space-5)"><span class="p-note__tag">read only</span>
        <span>You cannot draw a relation here. A dragged line would have nowhere to be written \u2014 there is no relation object in the descriptor, only the <code class="a-mono">ref</code> field, which is added in the entity's own editor.</span></div>
    </div>
  </div></div>`;
}

/* The model, drawn. Entities are placed in columns by how deep their
   references go \u2014 what nothing points out of on the left, what points at it
   to the right \u2014 so the arrows run one way and never cross a box. */
function entityMap() {
  const W = 256, HEAD = 42, ROW = 19, PAD = 12, COL = 132, GAP = 38;

  const depth = (e, seen = new Set()) => {
    if (seen.has(e.name)) return 0;
    seen.add(e.name);
    const refs = e.fields.filter((f) => f.type === 'ref');
    return refs.length ? 1 + Math.max(...refs.map((f) => depth(entity(f.entity), seen))) : 0;
  };

  const cols = [];
  ENTITIES.forEach((e) => {
    const d = depth(e);
    (cols[d] = cols[d] || []).push(e);
  });

  const box = {};
  let maxY = 0;
  cols.forEach((col, ci) => {
    let y = 0;
    col.forEach((e) => {
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

  const width = cols.length * W + (cols.length - 1) * COL;
  const height = maxY - GAP;

  const boxes = ENTITIES.map((e) => {
    const b = box[e.name];
    return `<g class="a-map__box" data-act="go" data-route="#/schema/${e.name}">
      <rect class="a-map__plate" x="${b.x}" y="${b.y}" width="${W}" height="${b.h}" rx="10"/>
      <path class="a-map__head" d="M${b.x} ${b.y + 10}a10 10 0 0 1 10-10h${W - 20}a10 10 0 0 1 10 10v${HEAD - 10}h-${W}z"/>
      <text class="a-map__name" x="${b.x + 12}" y="${b.y + 17}">${e.name}</text>
      <text class="a-map__meta" x="${b.x + 12}" y="${b.y + 30}">${e.tenancy} \u00b7 ${e.fields.length} fields \u00b7 ${e.rows.toLocaleString('en-US')} records</text>
      ${b.shown.map((f, i) => {
        const y = b.y + HEAD + i * ROW + 12;
        const isRef = f.type === 'ref';
        return `<text class="a-map__field${isRef ? ' a-map__field--ref' : ''}" x="${b.x + 12}" y="${y}">${f.name}</text>
          <text class="a-map__type" x="${b.x + W - 12}" y="${y}" text-anchor="end">${isRef ? '\u2192 ' + f.entity + ' \u00b7 ' + f.onDelete : f.type}</text>`;
      }).join('')}
      ${b.more > 0 ? `<text class="a-map__type" x="${b.x + 12}" y="${b.y + HEAD + b.shown.length * ROW + 8}">+${b.more} more</text>` : ''}
    </g>`;
  }).join('');

  const wires = ENTITIES.flatMap((e) => e.fields.filter((f) => f.type === 'ref').map((f) => {
    const from = box[e.name];
    const to = box[f.entity];
    if (!to) return '';
    const i = from.shown.findIndex((x) => x.name === f.name);
    const y1 = from.y + HEAD + (i < 0 ? from.shown.length - 1 : i) * ROW + 8;
    const y2 = to.y + 19;
    const x1 = from.x;                 // refs leave from the left edge, toward the parent
    const x2 = to.x + W;
    const mid = (x1 + x2) / 2;
    return `<path class="a-map__wire" d="M${x1} ${y1}C${mid} ${y1} ${mid} ${y2} ${x2} ${y2}"/>
      <circle class="a-map__dot" cx="${x1}" cy="${y1}" r="3"/>`;
  })).join('');

  return `<div class="a-map">
    <svg viewBox="-6 -6 ${width + 12} ${height + 12}" width="${width}" height="${height}" role="img"
      aria-label="Entity relationship map: work_orders points at customers and regions.">
      ${wires}${boxes}
    </svg>
  </div>`;
}

function relationRows() {
  const rels = [];
  ENTITIES.forEach((e) => e.fields.filter((f) => f.type === 'ref').forEach((f) => rels.push({ from: e.name, field: f.name, to: f.entity, onDelete: f.onDelete, required: f.required })));
  return rels.map((r) => `<div class="a-rel">
      <div class="a-rel__side"><span class="a-rel__entity">${r.from}</span><span class="a-rel__field">${r.field}${r.required ? ' · required' : ''}</span></div>
      <div class="a-rel__link"><span>many to one</span><span class="a-rel__wire"></span><span class="a-badge">on delete ${r.onDelete}</span></div>
      <div class="a-rel__side"><span class="a-rel__entity">${r.to}</span><span class="a-rel__field">id</span></div>
    </div>`).join('');
}

const TABS = [
  ['fields', 'Fields'], ['relationships', 'Relationships'], ['rules', 'Rules'],
  ['hooks', 'On write'], ['indexes', 'Indexes'], ['api', 'API'],
];

function screenEntity(name) {
  const e = entity(name);
  if (!e) return screenSchemaList();
  const tab = state.tab;

  const tabs = TABS.map(([t, label]) => {
    const n = t === 'fields' ? e.fields.length : t === 'hooks' ? e.hooks.length : t === 'indexes' ? e.indexes.length : 0;
    return `<button class="a-tab${t === tab ? ' a-tab--active' : ''}" data-act="tab" data-tab="${t}">${label}${n ? ` <span class="p-muted">${n}</span>` : ''}</button>`;
  }).join('');

  const body = {
    fields: fieldsTab, relationships: relationshipsTab, rules: rulesList,
    hooks: hooksTab, indexes: indexesTab, api: apiTab,
  }[tab](e);

  const wide = tab === 'api' || tab === 'hooks';
  const panel = `<div class="a-panel"><div class="a-tabs">${tabs}</div>${body}</div>`;

  return `${header([{ label: 'Schema', route: '#/schema' }, { label: name }], `
      <button class="a-btn p-hide-sm" data-act="overlay" data-kind="export">Export</button>
      <button class="a-btn" data-act="ai">${icon('spark')} Ask Alvo</button>
      <button class="a-btn a-btn--primary" data-act="go" data-route="#/schema/preview">Preview${state.pending ? ` (${state.pending})` : ''}</button>`)}
  <div class="a-content"><div class="a-stack">
    ${entityBar(name, '#/schema', `<button class="a-btn a-btn--sm" data-act="overlay" data-kind="new-entity">${icon('plus')} Entity</button>`)}

    <div class="p-between">
      <div><h1 class="a-page-title" style="font-family:var(--font-mono)">${name}</h1>
        <p class="p-muted p-tight" style="max-width:66ch">${esc(e.description)}</p></div>
      <div class="p-hstack">
        <span class="a-badge${e.tenancy === 'global' ? '' : ' a-badge--accent'}">${e.tenancy}</span>
        ${e.audit ? '<span class="a-badge a-badge--ok">versioned</span>' : ''}
        <a class="a-btn a-btn--sm" href="#/data/${name}">Browse ${e.rows.toLocaleString('en-US')} records</a></div>
    </div>

    ${wide ? `${panel}${state.pending ? pendingBar() : ''}`
      : `<div class="a-split a-split--wide">
        <div class="a-stack">${panel}${state.pending ? pendingBar() : ''}</div>
        <div class="a-split__aside">
          <div class="a-row">
            <span class="a-section-title" style="font-size:var(--text-sm)">Descriptor</span>
            <span class="p-muted">what apply will receive</span>
            <button class="a-btn a-btn--sm a-btn--ghost" style="margin-left:auto" data-act="overlay" data-kind="export">Copy</button></div>
          <pre class="a-json">${descriptorJson(e, state.selectedField)}</pre>
        </div>
      </div>`}
  </div></div>`;
}

function pendingBar() {
  return `<div class="a-pending">
    <span class="a-pending__count">${state.pending} changes not applied</span>
    <span class="p-muted">Nothing has reached the database. Preview shows the exact migration first.</span>
    <span style="margin-left:auto" class="p-hstack">
      <button class="a-btn a-btn--sm a-btn--ghost" data-act="discard">Discard</button>
      <button class="a-btn a-btn--sm a-btn--primary" data-act="go" data-route="#/schema/preview">Preview changes</button></span>
  </div>`;
}

function fieldsTab(e) {
  const rows = e.fields.map((f) => `<button class="a-fieldrow${state.selectedField === f.name ? ' a-fieldrow--active' : ''}" data-act="field" data-field="${f.name}">
      <span class="a-fieldrow__name">${f.name}</span>
      <span class="a-fieldrow__type">${typeLabel(f)}</span>
      <span class="a-fieldrow__flags">${flagBadges(f)}</span>
      <span class="a-fieldrow__drag" aria-hidden="true">⋮⋮</span></button>`).join('');

  return `<div class="a-toolbar">
      <input class="a-input" style="max-width:240px" placeholder="Filter fields" aria-label="Filter fields">
      <span class="p-muted">Selecting a field marks the lines it owns in the descriptor.</span>
      <span style="margin-left:auto" class="p-hstack">
        <button class="a-btn a-btn--sm" data-act="ai">${icon('spark')} Describe it instead</button>
        <button class="a-btn a-btn--sm a-btn--primary" data-act="field" data-field="__new">${icon('plus')} Add field</button></span>
    </div>${rows}
    <div class="a-row" style="padding:var(--space-3) var(--space-4);color:var(--faint);font-size:var(--text-xs)">
      <span>Alvo also maintains <code class="a-mono">id</code>, <code class="a-mono">created_at</code> and <code class="a-mono">updated_at</code>${e.audit ? ' and <code class="a-mono">version</code>' : ''}. You never declare them.</span>
    </div>`;
}

function relationshipsTab(e) {
  const out = e.fields.filter((f) => f.type === 'ref');
  const incoming = [];
  ENTITIES.forEach((o) => o.fields.filter((f) => f.type === 'ref' && f.entity === e.name).forEach((f) => incoming.push({ from: o.name, field: f.name, onDelete: f.onDelete })));

  const outHtml = out.length ? out.map((f) => `<div class="a-rel">
      <div class="a-rel__side"><span class="a-rel__entity">${e.name}</span><span class="a-rel__field">${f.name}</span></div>
      <div class="a-rel__link"><span>many to one</span><span class="a-rel__wire"></span><span class="a-badge">on delete ${f.onDelete}</span></div>
      <div class="a-rel__side"><span class="a-rel__entity">${f.entity}</span><span class="a-rel__field">id</span></div>
    </div>`).join('') : `<div class="a-empty"><span class="a-empty__title">Nothing points out of ${e.name}</span>
      <span class="a-empty__body">Add a field of type <code class="a-mono">ref</code> to connect this entity to another one. That field is the relationship — Alvo has no separate object for it.</span>
      <button class="a-btn a-btn--primary" data-act="field" data-field="__new">Add a ref field</button></div>`;

  const inHtml = incoming.length ? incoming.map((r) => `<div class="a-rel">
      <div class="a-rel__side"><span class="a-rel__entity">${r.from}</span><span class="a-rel__field">${r.field}</span></div>
      <div class="a-rel__link"><span>points here</span><span class="a-rel__wire"></span><span class="a-badge">on delete ${r.onDelete}</span></div>
      <div class="a-rel__side"><span class="a-rel__entity">${e.name}</span><span class="a-rel__field">id</span></div>
    </div>`).join('') + `<div class="p-note" style="margin:var(--space-4)"><span class="p-note__tag">why it matters</span>
      <span>Because something points here, a record's detail page can list what refers to it, and a field on ${e.name} can aggregate over them — <code class="a-mono">open_jobs</code> already does.</span></div>`
    : '<div class="a-empty"><span class="a-empty__body">No other entity points at this one yet.</span></div>';

  return `<div class="a-section" style="border-top:none"><span class="a-section-title">Out of ${e.name}</span>
      <span class="a-section-sub">One row per <code class="a-mono">ref</code> field.</span></div>${outHtml}
    <div class="a-section"><span class="a-section-title">Into ${e.name}</span>
      <span class="a-section-sub"><code class="a-mono">restrict</code> means a record here cannot be deleted while one of these points at it.</span></div>${inHtml}`;
}

function hooksTab(e) {
  if (!e.hooks.length) {
    return `<div class="a-empty"><span class="a-empty__title">Nothing happens on a write to ${e.name}</span>
      <span class="a-empty__body">A hook can refuse a write before it commits, fill a field in, or — once committed — send an email or call a webhook. Both kinds run today.</span>
      <button class="a-btn a-btn--primary">${icon('plus')} Add a hook</button></div>`;
  }
  const row = (h) => `<div class="a-hook">
      <span class="a-hook__point">
        <code class="a-mono" style="color:var(--text);font-size:var(--text-sm)">${h.point}</code>
        <span class="a-hook__when">${h.when}</span></span>
      <span style="min-width:0;display:flex;flex-direction:column;gap:var(--space-2)">
        ${h.condition ? `<span class="p-muted">only when <code class="a-mono" style="color:var(--text)">${esc(h.condition)}</code></span>` : '<span class="p-muted">on every write</span>'}
        <span class="a-row">
          <span class="a-badge${h.action.kind === 'reject' ? ' a-badge--danger' : h.action.kind === 'mutate' ? '' : ' a-badge--accent'}">${h.action.kind}</span>
          <span style="font-size:var(--text-sm)">${esc(h.action.text)}</span></span></span>
      <button class="a-btn a-btn--sm a-btn--ghost">Edit</button></div>`;

  return `<div class="a-section" style="border-top:none">
      <span class="a-section-title">Before the write commits</span>
      <span class="a-section-sub">In the same transaction. May refuse the write or change the values. No network — that is the guarantee, not a limitation.</span></div>
    ${e.hooks.filter((h) => h.point.startsWith('before')).map(row).join('')}
    <div class="a-section"><span class="a-section-title">After the write commits</span>
      <span class="a-section-sub">From the outbox, with retries. A failure here never rolls back the write that caused it.</span></div>
    ${e.hooks.filter((h) => h.point.startsWith('after')).map(row).join('')}
    <div class="p-note" style="margin:var(--space-4)"><span class="p-note__tag">honest</span>
      <span>Deliveries are not signed yet — <code class="a-mono">secretRef</code> is declared and not read, so a receiver cannot verify that Alvo sent it. Integrations says so too.</span></div>`;
}

function indexesTab(e) {
  if (!e.indexes.length) {
    return `<div class="a-empty"><span class="a-empty__title">No index beyond the primary key</span>
      <span class="a-empty__body">Add one when a column shows up in a filter or a sort you run often. ${e.name} is at ${e.rows.toLocaleString('en-US')} records.</span>
      <button class="a-btn a-btn--primary">${icon('plus')} Add index</button></div>`;
  }
  return `<div class="a-toolbar"><span class="p-muted">A composite index is ordered — the first column is the one a filter must name.</span>
      <button class="a-btn a-btn--sm a-btn--primary" style="margin-left:auto">${icon('plus')} Add index</button></div>
    ${e.indexes.map((ix) => `<div class="a-row" style="padding:var(--space-4);border-bottom:1px solid var(--border)">
      <code class="a-mono" style="font-size:var(--text-sm);color:var(--text)">${ix.fields.join(', ')}</code>
      <span class="p-muted">covers a filter on ${ix.fields[0]}${ix.fields.length > 1 ? `, then ${ix.fields.slice(1).join(', ')}` : ''}</span>
      <button class="a-btn a-btn--sm a-btn--ghost" style="margin-left:auto">Remove</button></div>`).join('')}`;
}

function apiTab(e) {
  const SAMPLES = {
    reference: 'WO-100418', title: 'Boiler will not fire on cold start',
    description: 'Fails on cold start, runs once warm.', code: 'BA-CENTRE',
    name: 'Nordreg Facilities', email: 'facilities@nordreg.sk',
  };
  const sample = { id: 'wo_7f31a' };
  e.fields.filter((f) => !f.hidden).slice(0, 6).forEach((f) => {
    sample[f.name] = SAMPLES[f.name] !== undefined ? SAMPLES[f.name]
      : f.type === 'enum' ? f.values[0]
      : f.type === 'integer' ? 1
      : f.type === 'decimal' ? 480.0
      : f.type === 'boolean' ? true
      : f.type === 'datetime' ? '2026-09-22T08:30:00Z'
      : f.type === 'date' ? '2026-09-22'
      : f.type === 'uuid' ? '9f1c\u20268a4e'
      : 'value';
  });

  const rule = (op) => celOf(rulesFor(e.name)[op], e);
  const routes = [
    ['GET', `/api/${e.name}`, 'List, filtered and sorted. Keyset paging.', rule('list')],
    ['GET', `/api/${e.name}/{id}`, 'One record.', rule('get')],
    ['POST', `/api/${e.name}`, 'Create. Idempotent with an Idempotency-Key header.', rule('create')],
    ['PATCH', `/api/${e.name}/{id}`, 'Change some fields.', rule('update')],
    ['DELETE', `/api/${e.name}/{id}`, 'Remove.', rule('delete')],
    ['POST', `/api/${e.name}/query`, 'The same read as GET, for filters too long for a URL.', rule('list')],
  ];

  return `<div class="a-toolbar"><span class="p-muted">Generated from this entity. Change a field and these change with it — there is no second place to update.</span>
      <span style="margin-left:auto" class="p-hstack">
        <button class="a-btn a-btn--sm">Open reference</button>
        <button class="a-btn a-btn--sm">Download OpenAPI</button></span></div>
    ${routes.map(([m, path, what, rule]) => `<div class="a-row" style="align-items:flex-start;padding:var(--space-3) var(--space-4);border-bottom:1px solid var(--border)">
      <span class="a-badge${m === 'GET' ? '' : m === 'DELETE' ? ' a-badge--danger' : ' a-badge--accent'}" style="flex:none;width:62px;justify-content:center">${m}</span>
      <span style="flex:1;min-width:0">
        <code class="a-mono" style="color:var(--text);font-size:var(--text-sm)">${path}</code>
        <span class="a-switcher-meta">${what}</span></span>
      <span style="flex:none;max-width:36%;text-align:right">
        ${rule ? `<code class="a-mono" style="font-size:var(--text-2xs)">${esc(rule.length > 42 ? rule.slice(0, 42) + '…' : rule)}</code>`
          : '<span class="a-badge a-badge--danger">no rule — refused</span>'}</span>
    </div>`).join('')}

    <div style="padding:var(--space-4);display:grid;grid-template-columns:repeat(auto-fit,minmax(300px,1fr));gap:var(--space-4)">
      <div class="a-stack" style="gap:var(--space-2)">
        <span class="a-label">Ask for the emergency jobs, newest first</span>
        <pre class="a-code">curl -H "Authorization: Bearer $ALVO_KEY" \\
  "$HOST/api/${e.name}?is_emergency=eq.true\\
&order=scheduled_for.desc&limit=20"</pre>
      </div>
      <div class="a-stack" style="gap:var(--space-2)">
        <span class="a-label">What comes back</span>
        <pre class="a-code">${highlight(JSON.stringify(sample, null, 2))}</pre>
        <span class="p-muted">${e.fields.filter((f) => f.hidden).length} hidden fields are in no response and in no published schema.</span>
      </div>
    </div>`;
}

/* ==========================================================================
   Rules
   ========================================================================== */

const OPS = [
  ['list', 'Read many', (n) => `GET /api/${n}`],
  ['get', 'Read one', (n) => `GET /api/${n}/{id}`],
  ['create', 'Create', (n) => `POST /api/${n}`],
  ['update', 'Change', (n) => `PATCH /api/${n}/{id}`],
  ['delete', 'Delete', (n) => `DELETE /api/${n}/{id}`],
];

/* ==========================================================================
   What a rule can actually say

   Checked against docs/architecture/cel.md, the Rule column of the profile
   table, and the rule strings the test suite compiles. A rule MAY:

     - test role membership            'x' in @user.roles          and negate it
     - compare a field of this row     status != 'cancelled'
       to a literal, to @user.id, to @tenant.id, or to another field
     - use a boolean field bare        is_public          and negate it
     - test presence                   has(scheduled_for)  !has(owner_id)
     - combine with && || ! and parentheses, freely

   It MAY NOT call a function. `endsWith`, `contains`, `matches` do not exist:
   any identifier before `(` other than `has`/`changed` is refused. And @user is
   a closed set of id and roles — there is no @user.email, so an attribute gate
   on a mail domain is not expressible in any block. baas-analyza §16.1 sketches
   exactly that rule; cel.md deviation 1 records the decision not to have it
   (#146), and examples/complex-crm/NOT-RUNNABLE.md records what it cost.

   So a rule is: alternatives joined by ||, each one a way IN that may be
   narrowed by tests that must all hold.

       (roleA && test) || (owner == @user.id && test && test)

   Conditions belong to the BRANCH, not to the column. "Dispatchers always,
   technicians only while the job is open" is the commonest rule anyone writes
   and a column-wide AND cannot express it.
   ========================================================================== */

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

function newCond(e, f) {
  const op = (OPERATORS[f.type] || OPERATORS.string)[0][0];
  if (NO_VALUE.includes(op)) return { field: f.name, op, value: '' };
  const opts = valueOptions(e, f);
  return { field: f.name, op, value: opts.length ? opts[0][0] : '1' };
}

const testable = (e) => e.fields.filter((f) => !f.hidden && OPERATORS[f.type] && !f.computed && !f.rollup);

/* What the right-hand side of a comparison may be. A literal, the caller, the
   caller's tenant, or another field of the same row — all four compile. */
function valueOptions(e, f) {
  const out = [];
  if (f.values) out.push(...f.values.map((v) => [v, v]));
  if (f.type === 'boolean') out.push(['true', 'true'], ['false', 'false']);
  if (f.type === 'uuid' || f.type === 'ref') out.push(['@user.id', 'the caller'], ['@tenant.id', "the caller's tenant"]);
  e.fields.filter((x) => x.type === f.type && x.name !== f.name && !x.hidden)
    .forEach((x) => out.push([`:${x.name}`, `the ${x.name} of this record`]));
  return out;
}

const isFieldRef = (v) => typeof v === 'string' && v.startsWith(':');
const isContextRef = (v) => v === '@user.id' || v === '@tenant.id';

function litOf(f, v) {
  if (isFieldRef(v)) return v.slice(1);
  if (isContextRef(v)) return v;
  if (f.type === 'boolean' || f.type === 'integer' || f.type === 'decimal') return String(v);
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

/* Emit the factored form when every branch carries the same tests — it is the
   same expression and it reads better. Otherwise emit per branch. */
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
  : { admin: 'Admins', dispatcher: 'Dispatchers', technician: 'Technicians' }[r]
    || r.charAt(0).toUpperCase() + r.slice(1) + 's');

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
    let who = b.kind === 'role' ? roleWord(b.role) : `whoever <code class="a-mono">${b.field}</code> names`;
    if (i && b.kind === 'role') who = who.charAt(0).toLowerCase() + who.slice(1);
    const w = (b.conds || []).map((c) => condWords(e, c));
    return w.length ? `${who} while ${w.join(' and ')}` : who;
  });
  if (parts.length === 1) return parts[0];
  /* Semicolons only once a branch carries its own condition \u2014 otherwise they
     make a plain list of roles look more complicated than it is. */
  const sep = bs.some((b) => (b.conds || []).length) ? ';' : ',';
  if (parts.length === 2) return `${parts[0]}${sep} or ${parts[1]}`;
  return `${parts.slice(0, -1).join(sep + ' ')}${sep} or ${parts[parts.length - 1]}`;
}

/* --- Reading an expression back ------------------------------------------
   The builder recognises exactly the shapes it can write and nothing more.
   Anything else sets raw: the controls step aside and say so, which keeps the
   round trip exact or absent, never approximate. */

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
  while (t.startsWith('(') && t.endsWith(')') && splitTop(t.slice(1, -1), '||').length >= 1) {
    const inner = t.slice(1, -1);
    let d = 0, ok = true;
    for (const ch of inner) { if (ch === '(') d++; if (ch === ')') d--; if (d < 0) ok = false; }
    if (!ok || d !== 0) break;
    t = inner.trim();
  }
  return t;
};

function parseWho(part) {
  const role = part.match(/^'([a-z0-9_-]+)' in @user\.roles$/);
  if (role) return { kind: 'role', role: role[1], conds: [] };
  const own = part.match(/^([a-z0-9_]+) == @user\.id$/);
  if (own) return { kind: 'owner', field: own[1], conds: [] };
  return null;
}

function parseCond(part) {
  let m = part.match(/^has\(([a-z0-9_]+)\)$/);
  if (m) return { field: m[1], op: 'has', value: '' };
  m = part.match(/^!has\(([a-z0-9_]+)\)$/);
  if (m) return { field: m[1], op: '!has', value: '' };
  m = part.match(/^([a-z0-9_]+) (==|!=|<=|>=|<|>) (.+)$/);
  if (m) {
    let v = m[3].trim();
    if (v.startsWith("'") && v.endsWith("'")) v = v.slice(1, -1);
    else if (/^[a-z0-9_]+$/.test(v) && !['true', 'false'].includes(v)) v = ':' + v;
    return { field: m[1], op: m[2], value: v };
  }
  m = part.match(/^!([a-z0-9_]+)$/);
  if (m) return { field: m[1], op: 'false', value: '' };
  m = part.match(/^([a-z0-9_]+)$/);
  if (m) return { field: m[1], op: 'true', value: '' };
  return null;
}

function parseCel(cel) {
  const empty = { branches: [], raw: null };
  if (!cel || !cel.trim()) return empty;
  const top = splitTop(cel, '&&');

  /* The factored form: (A || B) && c && c — distribute the tests into each. */
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

function rulesFor(name) {
  if (!state.rules[name]) {
    const ent = entity(name);
    state.rules[name] = Object.fromEntries(OPS.map(([op]) => [op, parseCel(ent.rules[op])]));
  }
  return state.rules[name];
}

const branchFor = (m, who) => (m.branches || []).find((b) => (who.kind === 'role' ? b.kind === 'role' && b.role === who.role : b.kind === 'owner' && b.field === who.field));

function rolesFor(model) {
  const used = new Set();
  Object.values(model).forEach((m) => (m.branches || []).forEach((b) => b.kind === 'role' && used.add(b.role)));
  const declared = catalog().filter((r) => !BUILTIN.includes(r));
  const stray = [...used].filter((r) => !BUILTIN.includes(r) && !declared.includes(r));
  return [...BUILTIN, ...declared, ...stray];
}

/* Sample records the simulator answers against, so a rule that tests a field
   has something real to test. */
function samplesFor(name) {
  if (name === 'work_orders') {
    return WORK_ORDERS.map((r) => ({
      id: r.id, label: `${r.reference} \u00b7 ${r.status.replace('_', ' ')}`, assignee: r.owner, tenant: 'nordreg',
      values: { status: r.status, priority: r.priority, is_emergency: r.emergency, quoted_price: r.quoted_price,
        scheduled_for: r.scheduled_for, completed_on: r.status === 'completed' ? '2026-09-19' : null,
        reference: r.reference, title: r.title, assigned_to: r.owner },
    }));
  }
  if (name === 'customers') {
    return CUSTOMERS.map((c) => ({ id: c.id, label: `${c.name} \u00b7 ${c.tier}`, assignee: null, tenant: 'nordreg',
      values: { tier: c.tier, open_jobs: c.open_jobs, name: c.name, email: c.email, phone: null, notes: null } }));
  }
  return REGIONS.map((r) => ({ id: r.id, label: r.code, assignee: null, tenant: null, values: { code: r.code, name: r.name } }));
}

/* --- Evaluation, for the simulator ---------------------------------------- */

function holdsCondition(e, c, rec, caller) {
  let v = rec.values[c.field];
  if (c.op === 'has') return v !== undefined && v !== null && v !== '';
  if (c.op === '!has') return v === undefined || v === null || v === '';
  if (c.op === 'true') return v === true;
  if (c.op === 'false') return v !== true;
  if (v === undefined) return null;
  const f = e.fields.find((x) => x.name === c.field);
  let want = c.value;
  if (isFieldRef(want)) want = rec.values[want.slice(1)];
  else if (want === '@user.id') want = caller.name;
  else if (want === '@tenant.id') want = rec.tenant;
  if (want === undefined) return null;
  if (f && (f.type === 'integer' || f.type === 'decimal')) { v = Number(v); want = Number(want); }
  /* Two-valued: a comparison with null is false, never unknown (cel.md). */
  if (v === null || want === null) return false;
  switch (c.op) {
    case '==': return v === want;
    case '!=': return v !== want;
    case '<=': return v <= want;
    case '>=': return v >= want;
    default: return null;
  }
}

function evaluate(m, caller, rec, e) {
  const bs = m.branches || [];
  if (!bs.length) return { ok: false, why: 'No rule is declared, so the operation is denied for everyone.' };
  const holds = [caller.role, 'authenticated'];
  let near = null;
  for (const b of bs) {
    const admits = b.kind === 'role' ? holds.includes(b.role) : !!rec && rec.assignee === caller.name;
    if (!admits) continue;
    const because = b.kind === 'role'
      ? `<code class="a-mono">'${b.role}' in @user.roles</code> is true`
      : `<code class="a-mono">${b.field} == @user.id</code> is true for this record`;
    const failed = (b.conds || []).find((c) => holdsCondition(e, c, rec, caller) === false);
    if (!failed) return { ok: true, why: `${because}${(b.conds || []).length ? ', and its conditions hold for this record' : ', so nothing after it is evaluated'}.` };
    near = `${because}, but <code class="a-mono">${condCel(e, failed)}</code> is not — this record's ${failed.field} is <code class="a-mono">${esc(String(rec.values[failed.field]))}</code>.`;
  }
  if (near) return { ok: false, why: near };
  const owner = bs.find((b) => b.kind === 'owner');
  return {
    ok: false,
    why: owner
      ? `The caller holds only <code class="a-mono">${caller.role}</code>, and <code class="a-mono">${owner.field}</code> names someone else.`
      : `The caller holds only <code class="a-mono">${caller.role}</code>, and no branch admits it.`,
  };
}

/* ==========================================================================
   The permissions screen
   ========================================================================== */

function permissionMatrix(e, model, roles) {
  const uuidFields = e.fields.filter((f) => f.type === 'uuid');
  const WORDS = Object.fromEntries(BUILTIN_ROLES);

  const cell = ({ on, via, when, act, data, fixed, pub }) => `<button
      class="a-cell${on ? ' a-cell--on' : ''}${via ? ' a-cell--via' : ''}${fixed ? ' a-cell--fixed' : ''}${pub ? ' a-cell--public' : ''}"
      ${fixed ? 'aria-disabled="true"' : `data-act="${act}" ${data}`} type="button" aria-pressed="${!!on}"
      ${via ? 'title="Granted through the record, not through this role"' : ''}>
      <span class="a-cell__mark">${on ? icon('check') : via ? 'own' : ''}</span>
      ${when ? `<span class="a-cell__when">${when}</span>` : ''}</button>`;

  const roleRow = (r) => `<tr>
      <td><span class="a-matrix__who">
        <span class="a-matrix__name">${r}</span>
        <span class="a-matrix__sub">${WORDS[r] ? esc(WORDS[r]) : `'${r}' in @user.roles`}</span></span></td>
      ${OPS.map(([op]) => {
        const m = model[op];
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
        <span class="a-matrix__name">Whoever the record names in ${f.name}</span>
        <span class="a-matrix__sub">${f.name} == @user.id · any signed-in caller</span></span></td>
      ${OPS.map(([op]) => {
        const m = model[op];
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

  const builtIn = roles.filter((r) => BUILTIN.includes(r));
  const declared = roles.filter((r) => !BUILTIN.includes(r));
  const isPublic = OPS.some(([op]) => branchFor(model[op], { kind: 'role', role: 'anon' }));

  return `<div class="a-matrix-wrap"><table class="a-matrix">
    <colgroup><col>${OPS.map(() => '<col class="a-matrix__opcol">').join('')}</colgroup>
    <thead><tr><th>Who</th>${OPS.map(([, verb]) => `<th>${verb}</th>`).join('')}</tr></thead>
    <tbody>
      <tr class="a-matrix__group"><td colspan="6">Built in · always present, never declared</td></tr>
      ${builtIn.map(roleRow).join('')}
      ${isPublic ? `<tr><td colspan="6" style="padding:0"><div class="a-public-warn">⚠
        <span>Anyone who can reach the URL may do that, signed in or not. Right for a public catalogue, wrong for everything else.</span></div></td></tr>` : ''}
      <tr class="a-matrix__group"><td colspan="6">Declared by this project · <a style="color:var(--accent)" href="#/access">manage in Access</a></td></tr>
      ${declared.map(roleRow).join('')}
      ${uuidFields.length ? `<tr class="a-matrix__group"><td colspan="6">Through the record itself</td></tr>${uuidFields.map(ownerRow).join('')}` : ''}
    </tbody>
  </table></div>`;
}

/* The compact read-only view, used inside the entity editor's Rules tab. */
function rulesList(e) {
  const model = rulesFor(e.name);
  return `<div class="a-toolbar">
      <span class="p-muted">Who may do each thing to these records, and when. An operation with no rule is refused for everyone — there is no implicit allow.</span>
      <a class="a-btn a-btn--sm" style="margin-left:auto" href="#/rules/${e.name}">Edit, and try it on someone</a></div>
    ${OPS.map(([op, verb, api]) => {
      const m = model[op];
      return `<div class="a-perm">
        <span class="a-perm__op"><span class="a-perm__verb">${verb}</span><span class="a-perm__api">${esc(api(e.name))}</span></span>
        <span><span class="a-perm__who">${sentenceOf(m, e)}</span>
          <span class="a-perm__cel">${esc(celOf(m, e)) || '// no rule'}</span></span>
        <span>${celOf(m, e) ? '' : '<span class="a-badge a-badge--danger">refused</span>'}</span>
      </div>`;
    }).join('')}`;
}

function ruleChanges(e) {
  const model = rulesFor(e.name);
  return OPS.filter(([op]) => celOf(model[op], e) !== (e.rules[op] || '')).map(([, verb]) => verb);
}

function screenRules(name) {
  const e = entity(name) || entity('work_orders');
  const model = rulesFor(e.name);
  const changed = ruleChanges(e);
  const samples = samplesFor(e.name);
  const caller = CALLERS.find((c) => c.role === state.simulate.role) || CALLERS[2];
  const rec = samples.find((r) => r.id === state.simulate.record) || samples[0];

  const rows = OPS.map(([op, verb, api]) => {
    const m = model[op];
    const open = state.ruleOpen === op;
    return `<div class="a-perm${open ? ' a-perm--open' : ''}" id="rule-${op}">
      <span class="a-perm__op"><span class="a-perm__verb">${verb}</span><span class="a-perm__api">${esc(api(e.name))}</span></span>
      <span><span class="a-perm__who">${sentenceOf(m, e)}</span>
        <span class="a-perm__cel">${esc(celOf(m, e)) || '// no rule — refused for everyone'}</span></span>
      <button class="a-btn a-btn--sm${open ? ' a-btn--primary' : ''}" data-act="ruleopen" data-op="${op}">${open ? 'Done' : 'Change'}</button>
      ${open ? ruleEditor(e, op, m) : ''}
    </div>`;
  }).join('');

  const verdicts = OPS.map(([op, verb]) => {
    const v = evaluate(model[op], caller, rec, e);
    const listNote = op === 'list' && !v.ok && (model[op].branches || []).length
      ? '<span class="a-sim__why">On a list this is not an error page: the caller gets the rows the rule does admit, filtered inside the query. Here that is none of them.</span>' : '';
    return `<div class="a-sim">
      <span>${verb}</span>
      <span class="a-badge${v.ok ? ' a-badge--ok' : ' a-badge--danger'}">${v.ok ? 'allowed' : op === 'list' ? 'filtered out' : 'refused'}</span>
      <span class="a-sim__why">${v.why}</span>${listNote}
    </div>`;
  }).join('');

  return `${header([{ label: 'Rules' }])}
  <div class="a-content"><div class="a-stack">
    ${entityBar(e.name, '#/rules', '<span class="p-muted">who may read and write each one</span>')}

    <div><h1 class="a-page-title">Who can do what to <span style="font-family:var(--font-mono)">${e.name}</span></h1>
      <p class="p-muted p-tight" style="max-width:74ch">A rule is a set of ways in, joined by <em>or</em>: a role, or the person the record names. Each way in can be narrowed by tests on the record that must all hold. Everything is checked inside the same transaction as the query, against the caller's id, their roles, their tenant, and this record's own fields — nothing else is reachable.</p></div>

    <div class="a-split a-split--wide">
      <div class="a-stack">
        <div class="a-panel">
          <div class="a-section"><span class="a-section-title">Permissions</span>
            <span class="a-section-sub">Tick to grant. Use Change below to narrow a grant.</span>
            <button class="a-btn a-btn--sm" style="margin-left:auto" data-act="ai">${icon('spark')} Ask Alvo</button></div>
          ${permissionMatrix(e, model, rolesFor(model))}
          <div class="a-row" style="padding:var(--space-3) var(--space-5);color:var(--faint);font-size:var(--text-xs)">
            <span>The five columns are all there are — Alvo generates exactly these operations from the entity. A role missing here is one the descriptor does not declare.</span>
          </div>
        </div>

        <div class="a-panel">
          <div class="a-section"><span class="a-section-title">What each one says</span>
            <span class="a-section-sub">The same five as sentences, and the CEL they produce.</span></div>
          ${rows}
        </div>
      </div>

      <div class="a-split__aside">
        <div class="a-card" style="gap:var(--space-4)">
          <div><span class="a-section-title">Try it on someone</span>
            <p class="p-muted p-tight">One caller, one record, all five answers.${changed.length ? ' <strong>Against your unapplied draft</strong> — callers still get revision 7.' : ''}</p></div>

          <div class="a-field"><span class="a-label">Signed in as</span>
            <div class="p-hstack">${CALLERS.map((c) => `<button class="a-preset${caller.role === c.role ? ' a-preset--on' : ''}" data-act="sim" data-k="role" data-v="${c.role}">${c.role}</button>`).join('')}</div>
            <span class="a-label__hint">${esc(caller.name)} · every signed-in caller also carries <code class="a-mono">authenticated</code>.</span></div>

          <div class="a-field"><span class="a-label">Looking at</span>
            <select class="a-select" data-act="simrec">${samples.map((s) => `<option value="${s.id}"${s.id === rec.id ? ' selected' : ''}>${esc(s.label)}${s.assignee ? ` · ${esc(s.assignee)}` : ''}</option>`).join('')}</select>
            <span class="a-label__hint">${rec.assignee === caller.name ? 'Assigned to this caller.' : rec.assignee ? `Assigned to ${esc(rec.assignee)}, not this caller.` : 'No assignee on this entity.'}</span></div>

          <div>${verdicts}</div>
        </div>
      </div>
    </div>
  </div></div>`;
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
    /* Two-valued nulls: a comparison with an empty value is false, and ! makes
       it true. An author negating a test must be told, not left to find out. */
    const trap = c.op === '!=' && !f.required;
    return `<div class="a-cond">
      <span class="a-cond__join">${i ? 'and' : 'only if'}</span>
      <select class="a-cond__part" data-act="condfield" data-op="${op}" data-entity="${e.name}" data-b="${bi}" data-i="${i}">
        ${fields.map((x) => `<option${x.name === c.field ? ' selected' : ''}>${x.name}</option>`).join('')}</select>
      <select class="a-cond__part" data-act="condop" data-op="${op}" data-entity="${e.name}" data-b="${bi}" data-i="${i}">
        ${ops.map(([o, w]) => `<option value="${o}"${o === c.op ? ' selected' : ''}>${w}</option>`).join('')}</select>
      ${needsValue ? (values.length
        ? `<select class="a-cond__part" data-act="condvalue" data-op="${op}" data-entity="${e.name}" data-b="${bi}" data-i="${i}">
            ${values.map(([v, w]) => `<option value="${esc(v)}"${String(v) === String(c.value) ? ' selected' : ''}>${esc(w)}</option>`).join('')}</select>`
        : `<input class="a-cond__part" style="width:92px" value="${esc(c.value)}" data-act="condvalue" data-op="${op}" data-entity="${e.name}" data-b="${bi}" data-i="${i}">`) : ''}
      <button class="a-btn a-btn--sm a-btn--ghost" data-act="condremove" data-op="${op}" data-entity="${e.name}" data-b="${bi}" data-i="${i}" type="button" aria-label="Remove">✕</button>
      ${trap ? `<div class="a-nulltrap" style="width:100%">⚠ <span>A record whose <code class="a-mono">${c.field}</code> is empty also passes this. A comparison against an empty value is false, and “is not” negates that to true.</span></div>` : ''}
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

  return `<div class="a-perm__editor a-form">
    ${raw ? '' : bs.length
      ? `<div><span class="a-label">Ways in<span class="a-label__hint">Any one of these admits the caller. Tick a cell in the matrix above to add one; narrow it here.</span></span>
          <div style="display:flex;flex-direction:column;gap:var(--space-2);margin-top:var(--space-2)">${bs.map(branch).join('')}</div></div>`
      : `<div class="a-error"><span class="a-error__title">Refused for everyone</span>
          <span class="a-error__detail">No way in is declared, so this operation is denied for every caller, an admin included.</span>
          <span class="a-error__fix">Tick a cell in the matrix above.</span></div>`}

    <div class="a-readout"><span class="a-readout__tag">cel</span><span>${esc(celOf(m, e)) || '// no rule — refused for everyone'}</span></div>

    <div class="a-row">
      <span class="p-muted">${raw ? 'Hand-written. The controls cannot represent it.' : 'The controls above produce this expression exactly.'}</span>
      <button class="a-btn a-btn--sm a-btn--ghost" style="margin-left:auto" data-act="ruleraw" data-op="${op}" data-entity="${e.name}">${raw ? 'Back to the controls' : 'Write the expression myself'}</button>
    </div>

    ${raw ? `<textarea class="a-cel__input" rows="3" aria-label="CEL expression">${esc(m.raw)}</textarea>
      <div class="a-cel__context">it may read
        <span class="a-cel__token">@user.id</span><span class="a-cel__token">@user.roles</span><span class="a-cel__token">@tenant.id</span>
        <span>and any field of this record. There is no <code class="a-mono">@user.email</code> and no function call — <code class="a-mono">has()</code> is the only one.</span></div>` : ''}
  </div>`;
}

/* ==========================================================================
   Preview / transfer
   ========================================================================== */

function diffBlock(rows) {
  return `<div class="a-diff">${rows.map(([k, t], i) => `<div class="a-diff__line${k === 'add' ? ' a-diff__line--add' : k === 'del' ? ' a-diff__line--del' : ''}">
    <span class="a-diff__gutter">${i + 1}</span><span>${k === 'add' ? '+' : k === 'del' ? '-' : ' '} ${esc(t)}</span></div>`).join('')}</div>`;
}

function screenPreview() {
  return `${header([{ label: 'Schema', route: '#/schema' }, { label: 'Preview changes' }])}
  <div class="a-content"><div class="a-stack" style="max-width:960px">
    <div><h1 class="a-page-title">Two changes, nothing applied</h1>
      <p class="p-muted p-tight">This came back from <code class="a-mono">PUT /management/descriptor?dryRun=true</code> — the same call the CLI makes. Nothing below has run.</p></div>

    <div class="a-panel">
      <div class="a-section"><span class="a-section-title">What changes in the descriptor</span></div>
      <div style="padding:var(--space-4)">${diffBlock([
        ['ctx', '    "quoted_price": {'], ['ctx', '      "type": "decimal",'],
        ['del', '      "precision": 10,'], ['add', '      "precision": 12,'],
        ['ctx', '      "scale": 2'], ['ctx', '    },'],
        ['ctx', '    "completed_by": {'], ['add', '      "type": "ref",'],
        ['add', '      "entity": "customers",'], ['add', '      "onDelete": "setNull"'], ['ctx', '    }'],
      ])}</div>
    </div>

    <div class="a-panel">
      <div class="a-section"><span class="a-section-title">What runs against the database</span>
        <span class="a-section-sub">In this order, in one transaction.</span></div>
      ${[
        ['ok', 'ALTER COLUMN', 'work_orders.quoted_price — widen decimal(10,2) to decimal(12,2)', 'Safe. No existing value loses precision.'],
        ['ok', 'ADD COLUMN', 'work_orders.completed_by — nullable ref to customers', 'Safe. Existing rows get NULL.'],
        ['warn', 'ADD CONSTRAINT', 'work_orders_completed_by_fkey — on delete set null', 'Takes a brief lock on work_orders (24,680 rows).'],
      ].map(([k, o, what, why]) => `<div class="a-row" style="align-items:flex-start;padding:var(--space-4);border-bottom:1px solid var(--border)">
        <span class="a-badge${k === 'warn' ? ' a-badge--warn' : ' a-badge--ok'}" style="flex:none;width:118px;justify-content:center">${o}</span>
        <span style="flex:1"><span style="font-size:var(--text-sm);font-family:var(--font-mono)">${esc(what)}</span>
          <span class="a-switcher-meta">${esc(why)}</span></span></div>`).join('')}
    </div>

    <div class="p-note"><span class="p-note__tag">design</span>
      <span>Nothing here destroys data, so no name has to be typed. The moment a preview contains a <code class="a-mono">DROP COLUMN</code> or a narrowing type change, this panel grows the red confirmation box and Apply stays disabled until the entity name is typed — the treatment rollback already gets.</span></div>

    <div class="a-row">
      <button class="a-btn a-btn--ghost" data-act="go" data-route="#/schema/work_orders">Back to the editor</button>
      <span style="margin-left:auto" class="p-hstack">
        <span class="p-muted">Applying writes revision 8.</span>
        <button class="a-btn a-btn--primary" data-act="apply">Apply these changes</button></span>
    </div>
  </div></div>`;
}

function screenTransfer() {
  return `${header([{ label: 'Schema', route: '#/schema' }, { label: 'Import / export' }])}
  <div class="a-content"><div class="a-stack" style="max-width:920px">
    <div><h1 class="a-page-title">The descriptor is the project</h1>
      <p class="p-muted p-tight" style="max-width:70ch">Everything you can click here lives in one JSON file. Export it into your repository and <code class="a-mono">alvo apply</code> reproduces this project exactly; import one and you get the diff before anything runs.</p></div>

    <div class="a-split">
      <div class="a-card">
        <span class="a-section-title">Export</span>
        <span class="p-muted">Revision 7, as applied. The two unapplied edits are not in it — preview and apply them first, or take the working copy instead.</span>
        <div class="a-row"><button class="a-btn a-btn--primary" data-act="overlay" data-kind="export">Download field-service.alvo.json</button>
          <button class="a-btn">Copy</button></div>
        <label class="a-row" style="gap:var(--space-2)"><span class="a-check" role="checkbox" aria-checked="false"></span>
          <span class="p-muted">Include the two unapplied edits</span></label>
      </div>
      <div class="a-card">
        <span class="a-section-title">Import</span>
        <span class="p-muted">Paste a descriptor or drop a file. It is checked against the schema and dry-run against this database before you are asked to apply anything.</span>
        <textarea class="a-textarea" placeholder='{ "apiVersion": "alvo.dev/v1", "name": "…" }'></textarea>
        <div class="a-row"><button class="a-btn a-btn--primary" data-act="go" data-route="#/schema/preview">Check this descriptor</button></div>
      </div>
    </div>

    <div class="a-panel">
      <div class="a-section"><span class="a-section-title">The four doors</span>
        <span class="a-section-sub">The same descriptor through any of them gives the same result. That is an acceptance criterion, not a slogan.</span></div>
      ${[['This dashboard', 'Clicks become the descriptor you see beside every editor.'],
         ['alvo apply', 'The CLI posts the same file to the same endpoint.'],
         ['The Management API', 'PUT /management/descriptor — what both of the above call.'],
         ['A repository file', 'GitOps: the file is the source, and boot applies it.']]
        .map(([k, v]) => `<div class="a-row" style="padding:var(--space-3) var(--space-5);border-bottom:1px solid var(--border)">
          <span style="flex:none;width:170px;font-size:var(--text-sm);font-weight:var(--weight-medium)">${k}</span>
          <span class="p-muted">${v}</span></div>`).join('')}
    </div>
  </div></div>`;
}

/* ==========================================================================
   Data
   ========================================================================== */

function screenDataList() {
  return `${header([{ label: 'Data' }])}
  <div class="a-content"><div class="a-stack">
    <div><h1 class="a-page-title">Data</h1>
      <p class="p-muted p-tight" style="max-width:70ch">Records go through the same API and the same rules your application uses. Nothing here bypasses a policy — you see exactly what your own account is allowed to see.</p></div>
    <div class="a-cards">
      ${ENTITIES.map((e) => `<div class="a-card a-card--action" data-act="go" data-route="#/data/${e.name}">
        <span class="a-row"><span class="a-section-title" style="font-family:var(--font-mono)">${e.name}</span>
          <span class="a-badge" style="margin-left:auto">${e.tenancy}</span></span>
        <span style="font-size:var(--text-xl);font-weight:var(--weight-bold);font-variant-numeric:tabular-nums">${e.rows.toLocaleString('en-US')}</span>
        <span class="p-muted">${e.fields.length} fields${e.fields.filter((f) => f.hidden).length ? ` · ${e.fields.filter((f) => f.hidden).length} never returned` : ''}</span>
      </div>`).join('')}
    </div>
  </div></div>`;
}

function screenData(name) {
  const e = entity(name) || entity('work_orders');
  const inner = state.screenState === 'loading' ? skeletonRows()
    : state.screenState === 'empty' ? `<div class="a-empty">
        <span class="a-empty__title">No ${e.name.replace('_', ' ')} match these filters</span>
        <span class="a-empty__body">One filter is active. Clear it to widen the search, or create the first record.</span>
        <span class="p-hstack"><button class="a-btn" data-act="state" data-state="ready">Clear filters</button>
        <button class="a-btn a-btn--primary" data-act="overlay" data-kind="record-new">New record</button></span></div>`
    : state.screenState === 'error' ? `<div style="padding:var(--space-5)"><div class="a-error">
        <span class="a-error__title">This filter names a field you cannot read</span>
        <span class="a-error__detail">internal_notes is hidden, so it appears in no response and can be named in no filter. The request was refused before it reached the database.</span>
        <span class="a-error__fix">Filter on description instead, or unhide the field in Schema.</span>
        <span class="a-error__type">https://alvo.dev/problems/unknown-field</span></div></div>`
    : e.name === 'customers' ? customerRows() : e.name === 'regions' ? regionRows() : workOrderRows();

  const bulk = state.selectedRows.size ? `<div class="a-bulkbar">
      <span style="font-weight:var(--weight-medium)">${state.selectedRows.size} selected</span>
      <button class="a-btn a-btn--sm">Set status</button>
      <button class="a-btn a-btn--sm">Assign technician</button>
      <button class="a-btn a-btn--sm">Export</button>
      <button class="a-btn a-btn--sm a-btn--danger" data-act="overlay" data-kind="bulk-delete">Delete</button>
      <button class="a-btn a-btn--sm a-btn--ghost" style="margin-left:auto" data-act="clear">Clear</button></div>` : '';

  return `${header([{ label: 'Data', route: '#/data' }, { label: e.name }], `
      <button class="a-btn a-btn--primary" data-act="overlay" data-kind="record-new">${icon('plus')} New record</button>`)}
  <div class="a-content"><div class="a-stack">
    ${entityBar(e.name, '#/data', `<a class="a-btn a-btn--sm" href="#/schema/${e.name}">Edit fields</a>`)}

    <div class="p-between">
      <div><h1 class="a-page-title" style="font-family:var(--font-mono)">${e.name}</h1>
        <p class="p-muted p-tight">Reading as <strong>jana@field-service.sk</strong>, admin. A technician would see only their own jobs.</p></div>
      <div class="p-hstack">
        ${e.tenancy === 'scoped' ? `<span class="p-bar__label">tenant</span>
          ${TENANTS.map((t) => `<button class="a-preset${state.tenant === t.id ? ' a-preset--on' : ''}" data-act="tenant" data-id="${t.id}">${t.name}</button>`).join('')}`
          : '<span class="a-badge">global — every tenant reads these rows</span>'}
      </div>
    </div>

    <div class="a-grid-wrap">
      <div class="a-toolbar">
        <input class="a-input" style="max-width:200px" placeholder="Search ${e.name}" aria-label="Search">
        <span class="a-chip">status is in_progress <span aria-hidden="true">✕</span></span>
        <span class="a-chip" style="border-style:dashed;color:var(--dim)">+ Add filter</span>
        <span style="margin-left:auto" class="p-hstack">
          <span class="p-bar__label">state</span>
          ${['ready', 'loading', 'empty', 'error'].map((s) => `<button class="a-preset${state.screenState === s ? ' a-preset--on' : ''}" data-act="state" data-state="${s}">${s}</button>`).join('')}</span>
      </div>
      ${bulk}${inner}
      <div class="a-row" style="padding:var(--space-3) var(--space-4);border-top:1px solid var(--border)">
        <span class="p-muted">showing ${Math.min(9, e.rows)} of ${e.rows.toLocaleString('en-US')} · keyset paging, so page 900 costs what page 1 costs</span>
        <span style="margin-left:auto" class="p-hstack">
          <button class="a-btn a-btn--sm" disabled>Previous</button>
          <button class="a-btn a-btn--sm">Next</button></span></div>
    </div>

    ${e.name === 'work_orders' ? `<div class="p-note"><span class="p-note__tag">honest</span>
      <span><code class="a-mono">internal_notes</code> and <code class="a-mono">access_code</code> are <code class="a-mono">hidden</code>: not columns you can add, not fields you can filter on. <code class="a-mono">access_code</code> still appears on the create form — required on write, unreadable afterwards, which is the one case a hidden field is named in a schema at all.</span></div>` : ''}
  </div></div>`;
}

function workOrderRows() {
  const rows = WORK_ORDERS.map((r) => {
    const on = state.selectedRows.has(r.id);
    return `<tr aria-selected="${on}">
      <td><span class="a-check${on ? ' a-check--on' : ''}" role="checkbox" aria-checked="${on}" data-act="pick" data-id="${r.id}">${on ? icon('check') : ''}</span></td>
      <td data-act="overlay" data-kind="record" data-id="${r.id}" style="cursor:pointer">
        <span style="font-size:var(--text-sm);font-weight:var(--weight-medium)">${esc(r.title)}</span>
        <span class="a-mono">${r.reference}</span></td>
      <td><a style="color:var(--accent)" href="#/data/customers">${esc(r.customer)}</a></td>
      <td>${statusBadge(r.status)}</td>
      <td class="a-num">${r.priority}</td>
      <td class="a-num">${eur(r.quoted_price)}</td>
      <td class="a-mono">${r.scheduled_for || '—'}</td></tr>`;
  }).join('');

  return `<table class="a-grid">
      <thead><tr><th style="width:44px"></th><th>Work order</th><th>Customer</th><th>Status</th><th class="a-num">Priority</th><th class="a-num">Quoted</th><th>Scheduled for</th></tr></thead>
      <tbody>${rows}</tbody></table>
    ${WORK_ORDERS.map((r) => `<div class="a-row-card" data-act="overlay" data-kind="record" data-id="${r.id}">
      <div class="a-row-card__head"><span>${esc(r.title)}</span>${statusBadge(r.status)}</div>
      <div class="a-row-card__meta"><span class="a-mono">${r.reference}</span><span>${esc(r.customer)}</span><span>${eur(r.quoted_price)}</span></div>
    </div>`).join('')}`;
}

function customerRows() {
  return `<table class="a-grid">
      <thead><tr><th>Customer</th><th>Tier</th><th>Email</th><th class="a-num">Open jobs</th></tr></thead>
      <tbody>${CUSTOMERS.map((c) => `<tr>
        <td><span style="font-size:var(--text-sm);font-weight:var(--weight-medium)">${esc(c.name)}</span><span class="a-mono">${c.id}</span></td>
        <td><span class="a-badge${c.tier === 'priority' ? ' a-badge--accent' : ''}">${c.tier}</span></td>
        <td class="a-mono">${esc(c.email)}</td>
        <td class="a-num">${c.open_jobs}</td></tr>`).join('')}</tbody></table>
    ${CUSTOMERS.map((c) => `<div class="a-row-card"><div class="a-row-card__head"><span>${esc(c.name)}</span><span class="a-badge">${c.tier}</span></div>
      <div class="a-row-card__meta"><span>${esc(c.email)}</span><span>${c.open_jobs} open</span></div></div>`).join('')}`;
}

function regionRows() {
  const rs = [['BA-CENTRE', 'Bratislava centre'], ['BA-WEST', 'Bratislava west'], ['KE-NORTH', 'Košice north'], ['ZA-EAST', 'Žilina east']];
  return `<table class="a-grid"><thead><tr><th>Code</th><th>Name</th></tr></thead>
      <tbody>${rs.map(([c, n]) => `<tr><td class="a-mono" style="color:var(--text)">${c}</td><td>${n}</td></tr>`).join('')}</tbody></table>
    ${rs.map(([c, n]) => `<div class="a-row-card"><div class="a-row-card__head"><span>${c}</span></div>
      <div class="a-row-card__meta"><span>${n}</span></div></div>`).join('')}`;
}

function skeletonRows() {
  return `<div style="padding:var(--space-4);display:flex;flex-direction:column;gap:var(--space-4)">
    ${Array.from({ length: 6 }, (_, i) => `<div class="a-row" style="gap:var(--space-4)">
      <div class="a-skeleton" style="height:14px;width:${34 - i * 2}%"></div>
      <div class="a-skeleton" style="height:14px;width:16%"></div>
      <div class="a-skeleton" style="height:14px;width:12%;margin-left:auto"></div></div>`).join('')}</div>`;
}

function statusBadge(s) {
  const map = { completed: 'a-badge--ok', in_progress: 'a-badge--accent', cancelled: 'a-badge--danger', scheduled: '' };
  return `<span class="a-badge ${map[s] || ''}">${s.replace('_', ' ')}</span>`;
}

/* ==========================================================================
   Integrations
   ========================================================================== */

function screenIntegrations() {
  return `${header([{ label: 'Integrations' }])}
  <div class="a-content"><div class="a-stack" style="max-width:960px">
    <div><h1 class="a-page-title">Integrations</h1>
      <p class="p-muted p-tight" style="max-width:72ch">Where a write leaves Alvo: an endpoint to post to, a message to send. Both are reachable from an entity's after-hooks today, and only from there — the automation rules that would also use them are not running yet.</p></div>

    <div class="a-panel">
      <div class="a-section"><span class="a-section-title">Webhook endpoints</span>
        <button class="a-btn a-btn--sm a-btn--primary" style="margin-left:auto">${icon('plus')} New endpoint</button></div>
      ${ENDPOINTS.map((p) => `<div style="padding:var(--space-4);border-bottom:1px solid var(--border);display:flex;flex-direction:column;gap:var(--space-2)">
        <div class="a-row">
          <code class="a-mono" style="color:var(--text);font-size:var(--text-sm)">${p.name}</code>
          ${p.usedBy.length ? `<span class="a-badge a-badge--ok">sent to by ${p.usedBy[0]}</span>` : '<span class="a-badge">nothing sends here</span>'}
          <button class="a-btn a-btn--sm a-btn--ghost" style="margin-left:auto">Edit</button></div>
        <span class="p-muted" style="overflow-wrap:anywhere">${esc(p.url)}</span>
        <div class="a-refused"><span class="a-row"><span class="a-badge a-badge--warn">not signed</span>
          <span class="p-muted"><code class="a-mono">secretRef: ${p.secretRef}</code> is declared and not read</span></span></div>
        <div class="a-refused__reason">⚠ <span>No Standard Webhooks HMAC header is sent, so the receiver cannot verify that Alvo sent this. Treat the endpoint as unauthenticated until signing lands.</span></div>
      </div>`).join('')}
    </div>

    <div class="a-panel">
      <div class="a-section"><span class="a-section-title">Message templates</span>
        <button class="a-btn a-btn--sm a-btn--primary" style="margin-left:auto">${icon('plus')} New template</button></div>
      ${TEMPLATES.map((t) => `<div style="padding:var(--space-4);border-bottom:1px solid var(--border);display:flex;flex-direction:column;gap:var(--space-2)">
        <div class="a-row">
          <code class="a-mono" style="color:var(--text);font-size:var(--text-sm)">${t.name}</code>
          ${t.usedBy.length ? `<span class="a-badge a-badge--ok">sent by ${t.usedBy[0]}</span>` : '<span class="a-badge">unused</span>'}
          ${t.bodyFile ? '<span class="a-notyet">body from a file — not read</span>' : ''}
          <button class="a-btn a-btn--sm a-btn--ghost" style="margin-left:auto">Edit</button></div>
        <span class="p-muted">${esc(t.subject)}</span>
      </div>`).join('')}
      <div class="p-note" style="margin:var(--space-4)"><span class="p-note__tag">honest</span>
        <span>A template an after-hook sends is rendered. A template referenced only from an automation rule is not, because no automation rule is evaluated — so “unused” here does not mean unused in your descriptor.</span></div>
    </div>

    <div class="a-panel">
      <div class="a-section"><span class="a-section-title">Events this project publishes</span>
        <span class="a-section-sub">One per entity and operation, in CloudEvents shape.</span></div>
      ${['entity.work_orders.created', 'entity.work_orders.updated', 'entity.work_orders.deleted', 'entity.customers.created']
        .map((ev) => `<div class="a-row" style="padding:var(--space-3) var(--space-5);border-bottom:1px solid var(--border)">
          <code class="a-mono" style="color:var(--text);font-size:var(--text-sm)">${ev}</code>
          <span class="p-muted" style="margin-left:auto">in-process subscribers only</span></div>`).join('')}
      <div class="p-note" style="margin:var(--space-4)"><span class="p-note__tag">not yet</span>
        <span>There is no delivery log to show: a subscriber receives an event in process and nothing records that it did. A log arrives with the automation engine, which is the thing that would make one worth reading.</span></div>
    </div>
  </div></div>`;
}

/* ==========================================================================
   Access — four questions, and they are not the same question

   1. Who is this person            identity store        immediate
   2. What roles do they hold       assignment: identity  immediate
                                    catalogue: descriptor  versioned
   3. What may they do with data    entities.*.rules       versioned
   4. What may they reach here      access                 versioned

   Alvo cannot answer (1) by creating anybody: IAlvoUserStore is Find, List and
   SetRoles. People arrive by signing in. And auth.providers is parsed and read
   by nothing in this build, so no provider picker is drawn.
   ========================================================================== */

const catalog = () => (state.roleCatalog ??= [...PROJECT.roles]);
const assignedTo = (u) => ((state.assigned ??= Object.fromEntries(USERS.map((x) => [x.email, [...x.roles]])))[u.email]);

const BUILTIN = BUILTIN_ROLES.map(([r]) => r);

/* Assigned \u2229 (declared \u222a built-in). A role outside that set is not refused
   anywhere \u2014 it is simply never minted, so nothing it is named in can match. */
const effectiveRoles = (u) => assignedTo(u).filter((r) => catalog().includes(r) || BUILTIN.includes(r));
const inertRoles = (u) => assignedTo(u).filter((r) => !catalog().includes(r) && !BUILTIN.includes(r));

/* Every signed-in caller carries `authenticated` on top of what they hold. */
const callerRoles = (u) => [...new Set([...effectiveRoles(u), 'authenticated'])];

const matchesPredicate = (pred, roles) => roles.some((r) => pred.includes(`'${r}'`));

/* Three independent predicates, highest match wins \u2014 not a hierarchy. */
function levelOf(u) {
  const roles = callerRoles(u);
  for (const l of ACCESS_LEVELS) if (matchesPredicate(l.predicate, roles)) return l;
  return null;
}

/* What this person may do with one entity's records, per operation. */
function canDo(e, u) {
  const model = rulesFor(e.name);
  const roles = callerRoles(u);
  return OPS.map(([op, verb]) => {
    const m = model[op];
    const bs = m.branches || [];
    if (!bs.length) return { verb, v: 'no', note: 'no rule' };
    const when = (b) => (b.conds || []).map((c) => condCel(e, c)).join(' and ');
    const byRole = bs.find((b) => b.kind === 'role' && roles.includes(b.role));
    if (byRole) return { verb, v: 'yes', note: when(byRole) ? `while ${when(byRole)}` : '' };
    const owner = bs.find((b) => b.kind === 'owner');
    if (owner) return { verb, v: 'own', note: `only where ${owner.field} is them${when(owner) ? `, while ${when(owner)}` : ''}` };
    return { verb, v: 'no', note: '' };
  });
}

function screenAccess() {
  const catalogChanged = catalog().length !== PROJECT.roles.length || catalog().some((r) => !PROJECT.roles.includes(r));

  const people = USERS.map((u) => {
    const lvl = levelOf(u);
    const inert = inertRoles(u);
    return `<tr>
      <td><span style="font-size:var(--text-sm);font-weight:var(--weight-medium)">${esc(u.name)}${u.self ? ' <span class="a-badge">you</span>' : ''}</span>
        <span class="a-mono">${esc(u.email)}</span></td>
      <td>
        ${assignedTo(u).map((r) => `<button class="a-badge${inert.includes(r) ? ' a-role--inert' : catalog().includes(r) ? ' a-badge--accent' : ''}"
            ${u.self ? 'aria-disabled="true"' : `data-act="unassign" data-email="${u.email}" data-role="${r}"`}
            title="${u.self ? 'You cannot change your own roles' : 'Remove'}" type="button">${r}${u.self ? '' : ' \u2715'}</button>`).join(' ')
          || '<span class="p-muted">no roles</span>'}
        ${u.self ? '' : `<button class="a-badge" style="border:1px dashed var(--border2);background:transparent" data-act="overlay" data-kind="assign" data-id="${u.email}" type="button">+</button>`}
        ${inert.length ? `<div class="a-refused__reason" style="margin-top:var(--space-2)">\u26a0 <span><code class="a-mono">${inert.join('</code>, <code class="a-mono">')}</code> ${inert.length > 1 ? 'are' : 'is'} assigned but not declared, so ${inert.length > 1 ? 'they match' : 'it matches'} nothing. Declare ${inert.length > 1 ? 'them' : 'it'} below, or remove ${inert.length > 1 ? 'them' : 'it'} here.</span></div>` : ''}
      </td>
      <td>${lvl
        ? `<span class="a-badge a-badge--ok">${lvl.level}</span>`
        : u.bootstrap ? '<span class="a-badge a-badge--ok">admin</span>' : '<span class="a-badge">cannot open the dashboard</span>'}</td>
      <td class="p-muted">${u.seen}</td>
      <td><button class="a-btn a-btn--sm" data-act="person" data-email="${u.email}">What they can do</button></td>
    </tr>`;
  }).join('');

  return `${header([{ label: 'Access' }])}
  <div class="a-content"><div class="a-stack">
    <div><h1 class="a-page-title">Access</h1>
      <p class="p-muted p-tight" style="max-width:74ch">Two different things live here and they move at different speeds. <strong>Who holds which role</strong> takes effect on the next request. <strong>Which roles exist, and what each unlocks</strong> is configuration \u2014 it changes the descriptor and waits for an apply, like any schema edit.</p></div>

    <div>
      <div class="a-band">
        <span class="a-band__title">People</span>
        <span class="a-band__when a-band__when--now">takes effect at once</span>
        <span class="a-band__note">Held in the identity store, not in the descriptor.</span>
      </div>
      <div class="a-panel">
        <table class="a-grid">
          <thead><tr><th>Person</th><th>Roles they hold</th><th>In this dashboard</th><th>Last seen</th><th></th></tr></thead>
          <tbody>${people}</tbody></table>
        ${USERS.map((u) => `<div class="a-row-card" data-act="person" data-email="${u.email}">
          <div class="a-row-card__head"><span>${esc(u.name)}</span><span class="p-muted">${u.seen}</span></div>
          <div class="a-row-card__meta"><span class="a-mono">${esc(u.email)}</span>${assignedTo(u).map((r) => `<span>${r}</span>`).join('')}</div></div>`).join('')}
        <div class="a-row" style="padding:var(--space-4) var(--space-5);color:var(--faint);font-size:var(--text-xs)">
          <span>Alvo does not create people here. Someone becomes a person on this list by signing in for the first time; this screen decides what they are once they have. Changing your own roles is refused \u2014 nobody promotes themselves.</span>
        </div>
      </div>
    </div>

    <div>
      <div class="a-band">
        <span class="a-band__title">Roles and levels</span>
        <span class="a-band__when a-band__when--later">reviewed before it applies</span>
        <span class="a-band__note">Part of the descriptor, so every change here is a revision you can roll back.</span>
      </div>

      <div class="a-split">
        <div class="a-panel">
          <div class="a-section"><span class="a-section-title">Roles this project declares</span>
            <button class="a-btn a-btn--sm a-btn--primary" style="margin-left:auto" data-act="overlay" data-kind="new-role">${icon('plus')} New role</button></div>
          ${catalog().map((r) => {
            const held = USERS.filter((u) => assignedTo(u).includes(r)).length;
            const usedBy = ENTITIES.filter((e) => OPS.some(([op]) => (rulesFor(e.name)[op].branches || []).some((b) => b.kind === 'role' && b.role === r))).map((e) => e.name);
            return `<div class="a-row" style="align-items:flex-start;padding:var(--space-4) var(--space-5);border-bottom:1px solid var(--border)">
              <span style="flex:1;min-width:0">
                <span style="font-size:var(--text-sm);font-weight:var(--weight-medium);font-family:var(--font-mono)">${r}</span>
                <span class="a-switcher-meta">${held} ${held === 1 ? 'person holds it' : 'people hold it'}${usedBy.length ? ` \u00b7 named in rules on ${usedBy.join(', ')}` : ' \u00b7 named in no rule'}</span></span>
              <button class="a-btn a-btn--sm a-btn--ghost" ${usedBy.length ? 'aria-disabled="true" title="Named in a rule \u2014 remove it there first"' : `data-act="delrole" data-role="${r}"`}>Remove</button>
            </div>`;
          }).join('')}
          <div style="padding:var(--space-4) var(--space-5)">
            <span class="a-label">Always present, never declared</span>
            <div class="p-hstack" style="margin-top:var(--space-2)">${BUILTIN_ROLES.map(([r, what]) => `<span class="a-badge" title="${esc(what)}">${r}</span>`).join('')}</div>
            <p class="p-muted p-tight" style="margin-top:var(--space-2)"><code class="a-mono">anon</code> is every caller with no identity at all \u2014 naming it in a rule is how something becomes public.</p>
          </div>
        </div>

        <div class="a-stack">
          <div class="a-panel">
            <div class="a-section"><span class="a-section-title">Who may use this dashboard</span></div>
            <div style="padding:var(--space-3) var(--space-4) 0;color:var(--dim);font-size:var(--text-xs)">Three independent tests, highest match wins. These govern the dashboard and the Management API \u2014 never data.</div>
            ${ACCESS_LEVELS.map((l) => `<div style="padding:var(--space-4);border-bottom:1px solid var(--border)">
              <div class="a-row" style="margin-bottom:var(--space-2)"><span class="a-badge a-badge--accent">${l.level}</span>
                <span class="p-muted">${l.grants}</span></div>
              <code class="a-code">${esc(l.predicate)}</code></div>`).join('')}
            <div style="padding:var(--space-4);color:var(--faint);font-size:var(--text-xs)">
              Matching none of the three means the dashboard will not open at all. The deployment's bootstrap administrator is an admin whatever this block says, so a project with no levels is still reachable \u2014 by one person.
            </div>
          </div>

          <div class="a-notyet-panel">
            <span class="a-notyet">Not yet</span>
            <span class="a-section-title">Teams</span>
            <span class="a-notyet-panel__body">A rule can name a role and the caller's own id. It cannot name a team, because <code class="a-mono">@user</code> exposes <code class="a-mono">id</code> and <code class="a-mono">roles</code> and nothing else \u2014 so a team here would let you draw a permission the engine could not enforce. Widening <code class="a-mono">@user</code> is additive, so today's roles keep working when it lands.</span>
          </div>
        </div>
      </div>

      ${catalogChanged ? `<div class="a-pending">
        <span class="a-pending__count">Role list changed</span>
        <span class="p-muted">${catalog().filter((r) => !PROJECT.roles.includes(r)).map((r) => `+${r}`).join(', ') || PROJECT.roles.filter((r) => !catalog().includes(r)).map((r) => `\u2212${r}`).join(', ')} \u2014 not applied. Assignments to it do nothing until it is.</span>
        <span style="margin-left:auto" class="p-hstack">
          <button class="a-btn a-btn--sm a-btn--ghost" data-act="rolereset">Discard</button>
          <button class="a-btn a-btn--sm a-btn--primary" data-act="go" data-route="#/schema/preview">Preview changes</button></span>
      </div>` : ''}
    </div>

    <div class="p-note"><span class="p-note__tag">two layers</span>
      <span>A role is a coarse answer \u2014 <em>Peter is a technician</em>. What a technician may do with a particular record is the fine one, and it lives in <a style="color:var(--accent)" href="#/rules">Rules</a>, per entity. They are complementary, not alternatives: without the second, a role either sees everything or nothing. <strong>What they can do</strong> on any row above shows both at once.</span></div>
  </div></div>`;
}

/* One person, read downward through every layer that governs them. */
function personDrawer(email) {
  const u = USERS.find((x) => x.email === email) || USERS[0];
  const lvl = levelOf(u);
  const inert = inertRoles(u);
  const eff = effectiveRoles(u);

  return `<div class="a-drawer a-drawer--wide"><div class="a-stack">
    <div class="p-between">
      <div><span class="a-page-title" style="font-size:var(--text-lg)">${esc(u.name)}</span>
        <p class="a-mono p-tight">${esc(u.email)}</p></div>
      <button class="a-btn a-btn--sm a-btn--ghost" data-act="close">Close</button>
    </div>

    <div class="a-ladder">
      <div class="a-rung a-rung--on">
        <span class="a-rung__label">How they got in</span>
        <span class="a-rung__value">Signed in with a password Alvo holds${u.bootstrap ? ', and is the deployment\u2019s bootstrap administrator' : ''}.</span>
        ${u.bootstrap ? '<span class="a-rung__why">A bootstrap administrator has full management access whatever the access block says. That is deployment configuration, not descriptor \u2014 it cannot be granted or removed from this screen.</span>' : ''}
      </div>

      <div class="a-rung${eff.length ? ' a-rung--on' : ''}">
        <span class="a-rung__label">What they are</span>
        <span class="a-rung__value">${eff.length ? eff.map((r) => `<span class="a-badge a-badge--accent">${r}</span>`).join(' ') : '<span class="p-muted">No role that counts.</span>'}
          ${inert.map((r) => `<span class="a-badge a-role--inert">${r}</span>`).join(' ')}</span>
        <span class="a-rung__why">Every signed-in caller also carries <code class="a-mono">authenticated</code>.
          ${inert.length ? `<strong>${inert.join(', ')}</strong> is assigned but the descriptor does not declare it, so it is never minted and matches nothing \u2014 neither here nor in any rule.` : ''}</span>
      </div>

      <div class="a-rung${lvl || u.bootstrap ? ' a-rung--on' : ''}">
        <span class="a-rung__label">In this dashboard</span>
        <span class="a-rung__value">${lvl ? `<strong>${lvl.level}</strong> \u2014 ${lvl.grants.charAt(0).toLowerCase() + lvl.grants.slice(1)}`
          : u.bootstrap ? '<strong>admin</strong> \u2014 by bootstrap, not by the descriptor.'
          : '<strong>Cannot open it.</strong> The dashboard refuses every management operation for them.'}</span>
        <span class="a-rung__why">${lvl ? `Matched <code class="a-mono">${esc(lvl.predicate)}</code>. Levels govern the dashboard and the Management API only \u2014 never data.`
          : 'No access level admits any role they hold. Give them one of the roles a level names, or widen a level.'}</span>
      </div>

      <div class="a-rung a-rung--on">
        <span class="a-rung__label">With data</span>
        <span class="a-rung__why">Decided per entity by its rules, through the ordinary Data API \u2014 the same answer their own application gets. A dashboard level changes nothing here.</span>
        ${ENTITIES.map((e) => {
          const rows = canDo(e, u);
          const notes = rows.filter((r) => r.note).map((r) => `${r.verb.toLowerCase()}: ${r.note}`);
          return `<div style="margin-top:var(--space-3)">
            <div class="a-can a-can--head"><span>${e.name}</span>${rows.map((r) => `<span class="a-can__v">${r.verb.split(' ')[0]}${r.verb.includes(' ') ? ' ' + r.verb.split(' ')[1] : ''}</span>`).join('')}</div>
            <div class="a-can"><span class="p-muted">${e.rows.toLocaleString('en-US')} records</span>
              ${rows.map((r) => `<span class="a-can__v a-can__v--${r.v}">${r.v === 'yes' ? '\u2713' : r.v === 'own' ? 'own' : '\u2014'}</span>`).join('')}
              ${notes.length ? `<span class="a-can__note">${esc(notes.join(' \u00b7 '))}</span>` : ''}</div>
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
   Access, history, settings, not-yet
   ========================================================================== */

function screenHistory() {
  return `${header([{ label: 'Configuration history' }])}
  <div class="a-content"><div class="a-stack">
    <div><h1 class="a-page-title">Configuration history</h1>
      <p class="p-muted p-tight" style="max-width:72ch">Every apply appends a revision recording who, when and why. Nothing here is edited or removed — undoing a change writes a new revision that points back at the one it restored.</p></div>

    <div class="p-note"><span class="p-note__tag">scope</span>
      <span>This is the history of the <em>configuration</em>. Who changed which work order is a separate log, and it does not exist yet — it joins as a second tab rather than being implied here.</span></div>

    <div class="a-split a-split--wide">
      <div class="a-panel">
        <div class="a-section"><span class="a-section-title">Revisions</span>
          <span class="a-section-sub">Comparing r${state.compareB} with r7</span></div>
        ${REVISIONS.map((r) => `<div class="a-row" style="align-items:flex-start;padding:var(--space-4);border-bottom:1px solid var(--border);${[7, state.compareB].includes(r.revision) ? 'background:var(--accentSoft)' : ''}">
          <span class="a-badge${r.rolledBackFrom ? ' a-badge--warn' : ''}" style="flex:none">r${r.revision}</span>
          <span style="flex:1;min-width:0"><span style="font-size:var(--text-sm)">${esc(r.reason)}</span>
            <span class="a-switcher-meta">${esc(r.author)} · ${r.at}${r.rolledBackFrom ? ` · restored r${r.rolledBackFrom - 1}` : ''}</span></span>
          <span class="p-hstack" style="flex:none">
            <button class="a-btn a-btn--sm a-btn--ghost" data-act="compare" data-rev="${r.revision}">Compare</button>
            ${r.revision !== 7 ? `<button class="a-btn a-btn--sm" data-act="overlay" data-kind="rollback" data-id="${r.revision}">Restore</button>` : '<span class="a-badge a-badge--ok">current</span>'}
          </span></div>`).join('')}
      </div>
      <div class="a-split__aside">
        <div class="a-row"><span class="a-section-title" style="font-size:var(--text-sm)">r${state.compareB} → r7</span></div>
        ${diffBlock([
          ['ctx', '  "work_orders": {'], ['ctx', '    "fields": {'],
          ['add', '      "access_code": {'], ['add', '        "type": "string",'],
          ['add', '        "required": true,'], ['add', '        "hidden": true,'],
          ['add', '        "maxLength": 32'], ['add', '      },'],
          ['ctx', '      "customer_id": { "type": "ref", … }'],
        ])}
      </div>
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
      <span class="a-notyet-panel__consequence">${esc(WARNED[key])}</span>
      <span class="p-hstack"><button class="a-btn" data-act="go" data-route="#/schema/transfer">Declare it anyway</button></span>
    </div>
    <div class="p-note"><span class="p-note__tag">design</span><span>${later}</span></div>
  </div></div>`;
}

function screenSettings() {
  return `${header([{ label: 'Settings' }])}
  <div class="a-content"><div class="a-stack" style="max-width:920px">
    <div><h1 class="a-page-title">Settings</h1></div>

    <div class="a-panel">
      <div class="a-section"><span class="a-section-title">This instance</span></div>
      <div style="padding:var(--space-5)"><dl class="p-kv">
        <dt>Version</dt><dd class="a-mono">${PROJECT.version}</dd>
        <dt>Mode</dt><dd>${PROJECT.mode} — this dashboard and the Management API in one container</dd>
        <dt>Database</dt><dd>${PROJECT.engine}</dd>
        <dt>Multi-tenancy</dt><dd>enabled — 2 tenants; scoped entities carry one, global entities do not</dd>
        <dt>Descriptor source</dt><dd>database record, editable here. Point it at a repository file and schema becomes read-only in this dashboard.</dd>
        <dt>Health</dt><dd><span class="a-badge a-badge--ok"><span class="a-dot"></span> ready</span> <span class="p-muted">/health/ready · /health/live</span></dd>
      </dl></div>
    </div>

    <div class="a-panel">
      <div class="a-section"><span class="a-section-title">Assistant</span>
        <span class="a-section-sub">Which model answers, and what it is allowed to reach.</span></div>
      <div style="padding:var(--space-5)" class="a-form">
        <div class="a-field"><span class="a-label">Model<span class="a-label__hint">Configured on the instance, the way the connection string is. A key typed into this page instead would need somewhere safe to keep it, and that store does not exist yet — which is why this is instance configuration and not a text box.</span></span>
          <div class="a-row"><code class="a-code" style="flex:1">Alvo:Ai:Model = claude-sonnet-5</code><span class="a-badge a-badge--ok">key present</span></div></div>
        <div class="a-field"><span class="a-label">What it may call<span class="a-label__hint">Read-only management operations and the query endpoint. There is no write path that does not go through your approval.</span></span>
          <div class="a-panel" style="padding:var(--space-3)">
            ${AI_TOOLS.map(([what, how, mode]) => `<div class="a-row" style="padding:var(--space-2) var(--space-2)">
              <span style="flex:none;width:44px"><span class="a-badge${mode === 'never' ? ' a-badge--danger' : ' a-badge--ok'}">${mode === 'never' ? 'no' : 'yes'}</span></span>
              <span style="flex:1;font-size:var(--text-sm)">${what}</span>
              <code class="a-mono">${esc(how)}</code></div>`).join('')}
          </div></div>
        <label class="a-row" style="gap:var(--space-3)"><span class="a-toggle a-toggle--on" role="switch" aria-checked="true"></span>
          <span><span style="font-size:var(--text-sm)">Let it read record data when you ask about records</span>
            <span class="a-switcher-meta">Off means it answers from the schema and the rules only</span></span></label>
      </div>
    </div>

    <div class="a-panel">
      <div class="a-section"><span class="a-section-title">API keys</span>
        <span class="a-section-sub">A key's scopes gate the data API. They do not gate this dashboard — roles do.</span>
        <button class="a-btn a-btn--sm a-btn--primary" style="margin-left:auto" data-act="overlay" data-kind="new-key">${icon('plus')} New key</button></div>
      <table class="a-grid">
        <thead><tr><th>Name</th><th>Key</th><th>Scopes</th><th>Last used</th><th></th></tr></thead>
        <tbody>${API_KEYS.map((k) => `<tr>
          <td style="font-size:var(--text-sm);font-weight:var(--weight-medium)">${k.name}</td>
          <td class="a-mono">${k.prefix}</td>
          <td>${k.scopes.map((s) => `<span class="a-badge">${s}</span>`).join(' ')}</td>
          <td class="p-muted">${k.lastUsed}</td>
          <td><button class="a-btn a-btn--sm a-btn--ghost">Revoke</button></td></tr>`).join('')}</tbody></table>
      ${API_KEYS.map((k) => `<div class="a-row-card"><div class="a-row-card__head"><span>${k.name}</span><span class="a-mono">${k.prefix}</span></div>
        <div class="a-row-card__meta">${k.scopes.map((s) => `<span>${s}</span>`).join('')}</div></div>`).join('')}
    </div>

    <div class="a-panel" style="border-color:var(--danger-fg)">
      <div class="a-section" style="border-color:var(--danger-fg)"><span class="a-section-title" style="color:var(--danger-fg)">Delete this project</span></div>
      <div style="padding:var(--space-5);display:flex;flex-direction:column;gap:var(--space-3)">
        <span class="p-muted">Removes the descriptor, every revision and every table Alvo created for it — 26,532 records. There is no undo and no export afterwards.</span>
        <div><button class="a-btn a-btn--danger" data-act="overlay" data-kind="delete-project">Delete field-service</button></div>
      </div>
    </div>
  </div></div>`;
}

/* ==========================================================================
   Welcome
   ========================================================================== */

function screenWelcome() {
  const step = Number(state.route.split('/')[2] || 1);
  const steps = ['Your account', 'The project'].map((label, i) => {
    const n = i + 1;
    const cls = n < step ? 'a-step--done' : n === step ? 'a-step--now' : '';
    return `<span class="a-step ${cls}"><span class="a-step__dot">${n < step ? '✓' : n}</span>${label}</span>${i < 1 ? '<span class="a-step__rule"></span>' : ''}`;
  }).join('');

  const bodies = {
    1: `<div class="a-card a-form">
        <div><span class="a-section-title">Create the first account</span>
          <p class="p-muted p-tight">This one gets in before any rule exists, so it is the account that can write the first one. Everything it does afterwards is ordinary — there is no permanent back door.</p></div>
        <div class="a-field"><span class="a-label">Email</span><input class="a-input" id="w-email" value="jana@field-service.sk"></div>
        <div class="a-field"><span class="a-label">Password<span class="a-label__hint">Alvo refuses to start with the default password still set. This is the only screen that can change it.</span></span>
          <input class="a-input" id="w-pass" type="password" value="••••••••••••"></div>
      </div>`,
    2: `<div class="a-card a-form">
        <div><span class="a-section-title">Name the project, or bring one</span>
          <p class="p-muted p-tight">A project is one descriptor. If you already have one, import it and you are finished here.</p></div>
        <div class="a-field"><span class="a-label">Project name</span><input class="a-input" id="w-name" value="field-service"></div>
        <div class="a-field"><span class="a-label">What it is<span class="a-label__hint">Becomes the descriptor's description, and the summary of the generated OpenAPI document.</span></span>
          <textarea class="a-textarea" id="w-desc" style="min-height:64px">A multi-tenant field-service dispatch backend.</textarea></div>
        <label class="a-row" style="gap:var(--space-3)"><span class="a-toggle a-toggle--on" role="switch" aria-checked="true"></span>
          <span><span style="font-size:var(--text-sm)">Separate each customer's data</span>
            <span class="a-switcher-meta">multi-tenancy — hard to add later, free now</span></span></label>
        <div class="a-row"><button class="a-btn" data-act="go" data-route="#/schema/transfer">I already have a descriptor</button></div>
      </div>`,
  };

  return `<div class="a-wizard">
    <img src="alvo-wordmark.svg" width="168" height="40" alt="Alvo — backend as a service">
    <div>
      <h1 class="a-page-title" style="font-size:var(--text-2xl)">${step === 1 ? 'Nothing is configured yet' : 'What are you building?'}</h1>
      <p class="p-muted p-tight">${step === 1
        ? 'Two steps, and you have a running backend with a REST API you can model from the dashboard.'
        : 'This becomes the descriptor — the one file that reproduces everything you do here.'}</p>
    </div>
    <div class="a-steps">${steps}</div>
    ${bodies[step]}
    <div class="a-row">
      ${step > 1 ? `<button class="a-btn a-btn--ghost" data-act="go" data-route="#/welcome/1">${icon('back')} Back</button>` : ''}
      <button class="a-btn a-btn--primary" style="margin-left:auto" data-act="go" data-route="${step < 2 ? '#/welcome/2' : '#/schema'}">
        ${step < 2 ? 'Continue' : 'Create it and start modelling'}</button>
    </div>
    ${step === 2 ? '<p class="p-muted p-tight">Your first entity comes next, in the schema editor — the same screen you will use to change it tomorrow. A wizard step for it would be a second, worse copy of that screen.</p>' : ''}
  </div>`;
}

/* ==========================================================================
   Design notes
   ========================================================================== */

function screenNotes() {
  const components = [
    ['a-split / a-json', 'Model on the left, the descriptor it produces on the right.', 'new'],
    ['a-fieldrow', 'One field: name, type, flag strip, reorder handle.', 'new'],
    ['a-form', 'Form scale — one step up from the grid scale. 13 px label, 15 px control.', 'new'],
    ['a-typegrid / a-typechip', 'The eleven field types as one uniform grid, with the selected one explained beneath.', 'new'],
    ['a-entitybar', 'Switch entity without leaving the editor.', 'new'],
    ['a-rel', 'A relationship, shown as the ref field it actually is.', 'new'],
    ['a-map', 'The model drawn: entity boxes, and an arrow from each ref field to what it points at.', 'new'],
    ['a-matrix / a-cell', 'Roles down, operations across, a cell you tick. Built-ins apart, anon marked, a granted-through-the-record marker, a condition count.', 'new'],
    ['a-branch / a-nulltrap', 'One way in and the tests that narrow it, plus the null-comparison warning.', 'new'],
    ['a-band', 'The line between what takes effect at once and what waits for an apply.', 'new'],
    ['a-ladder / a-rung / a-can', 'One person read downward through identity, roles, dashboard level and data.', 'new'],
    ['a-perm / a-perm__editor', 'The five rules as sentences, with the full editor inside the row.', 'new'],
    ['a-sim', 'One caller, five verdicts.', 'new'],
    ['a-presets / a-readout', 'Multi-select chips, and the CEL the controls produced.', 'new'],
    ['a-verdict', 'The simulator’s answer, and which rule decided it.', 'new'],
    ['a-picker / a-subgrid', 'Choosing a referenced record, and listing the records that point back.', 'new'],
    ['a-hook', 'A lifecycle hook, before or after the commit.', 'new'],
    ['a-disclose', 'A field’s own JSON, one click away.', 'new'],
    ['a-ai', 'The assistant drawer and its proposal card.', 'new'],
    ['a-pending', 'The unapplied-changes bar. Sticky, never a toast.', 'new'],
    ['a-wizard / a-steps / a-reveal', 'First run, and a key shown once.', 'new'],
    ['a-shell / a-sidebar / a-header / a-nav', 'Chrome.', 'exists'],
    ['a-grid / a-row-card / a-bulkbar / a-toolbar', 'The data grid and its 375 px substitute.', 'exists'],
    ['a-diff', 'Dry run, revision compare, and the assistant’s proposal. Three consumers, one component.', 'exists'],
    ['a-notyet / a-notyet-panel / a-refused', 'The two classes of “not yet”.', 'exists'],
    ['a-error / a-empty / a-skeleton / a-toast', 'Feedback.', 'exists'],
    ['a-palette / a-modal / a-drawer / a-confirm', 'Overlays.', 'exists'],
  ];

  const decisions = [
    ['The dashboard is a modelling tool first, so Schema sits above Data.',
     'The reason to open it is to define what you keep track of — entities, fields, types, relationships. Browsing records proves the model works; it is not why the tool exists. The written design put Data first; this reverses it.'],
    ['Every schema editor is a split: model on the left, descriptor on the right.',
     '“Everything clickable is exportable as code” is an acceptance criterion. Showing the JSON as it is built turns it from a claim into something you watch happen, and makes the dry-run diff unsurprising.'],
    ['The field type is eleven visible buttons in one uniform grid, not a select and not seven groups.',
     'A select hides ten of eleven choices behind a click. Grouping them by kind sounded right and looked terrible — seven ragged rows, four times the height, eleven micro-captions competing for attention. Equal chips on a shared baseline scan in a glance, and the explanation belongs to the type you picked, once, underneath. The reference drawing had this right first.'],
    ['The field editor is eleven forms, and the schema says which.',
     'Picking a type does not only set a value — it changes what the rest of the drawer may contain, and the frozen schema pins that exactly. Three types (decimal, enum, ref) carry settings that are required, so those render as a block the form will not let you leave empty rather than as optional fields; seven types carry none, and render none. The table below is the spec for it, because an implementation that covers four branches and silently renders nothing for the other seven looks identical to one that is finished.'],
    ['Forms use a scale one step above the grid, in a wider drawer.',
     'alvo.css sets 13 px controls, correct for a grid read in bulk and too small for a focused editor — the first prototype’s field drawer was measurably harder to read than the reference drawing. .a-form raises label and control one step. No new token.'],
    ['An entity bar rides above every schema, rules and data screen.',
     'Comparing two entities’ fields used to cost three moves: back, pick, forward. It is the most common thing anyone does while modelling.'],
    ['Access is split by how fast a change takes effect, not by which store holds it.',
     'Assigning a role to a person happens in the identity store and counts on their next request. Declaring a role changes the descriptor and waits for an apply. On one undifferentiated screen half the controls would lie about when they work, so the boundary is drawn once and labelled in words \u2014 takes effect at once, reviewed before it applies \u2014 with its own pending bar on the half that needs one.'],
    ['There is no Invite button, because IAlvoUserStore cannot create anybody.',
     'It is Find, FindByEmail, List and SetRoles. People arrive by signing in; this screen decides what they are once they have. The first prototype drew an Invite control whose only possible output was nothing \u2014 the same defect the refused-feature rule exists to catch, committed in my own design.'],
    ['A role that is assigned but not declared is shown as inert, not as an error.',
     'Effective roles are assigned \u2229 declared, and the catalogue provider fails closed. Nothing refuses such a role anywhere \u2014 it is simply never minted, so every rule naming it silently never matches. That is the worst kind of quiet, so the person row and the ladder both call it out and offer the two ways out: declare it, or remove it.'],
    ['Every person row opens their access, read downward through all three layers.',
     'The analysis asks the framework to communicate that roles and row rules are complementary layers rather than alternatives. Saying it in prose does not work; showing one person \u2014 how they signed in, what roles count, what the dashboard lets them near, and then per entity what they may actually do with records \u2014 does. It is also the only place that answers \u201ccan this person open the dashboard at all\u201d, which is a different question from \u201cwhat can they read\u201d.'],
    ['Changing your own roles is refused.',
     'An adversarial acceptance criterion in the analysis: nobody promotes themselves. Your own row shows its roles without a remove control and without the add button.'],
    ['The matrix separates built-in roles from declared ones, and anon is never quiet.',
     'anon, authenticated and admin always exist and are never declared, so they are their own group above the project\u2019s own roles. Ticking anon makes an operation reachable by anyone who can reach the URL: the cell turns amber, a warning appears under the group, and the sentence stops listing the other branches because anon subsumes them all. An open rule is the failure mode the sources single out; it should not look like every other tick.'],
    ['No provider picker, because auth.providers is read by nothing.',
     'The block is parsed into the descriptor model and has no consumer anywhere in the build. Drawing a control over it would be a setting that changes nothing \u2014 identity is whatever the host configured.'],
    ['Rules open with a matrix: roles down, operations across, tick a cell.',
     'Three times I answered “how do I add a permission” with prose, and three times it did not land — which meant the screen was wrong, not the reader. People think in permissions (this role may do that), not in rules (this operation has an expression). The matrix is that thought, and one tick does the whole loop: the sentence changes, the CEL changes, the simulator flips, the pending bar appears.'],
    ['A roles-by-operations matrix is allowed; a teams-by-entities one is not.',
     'I ruled a permission matrix out earlier and that ruling still holds — for TEAMS, which @user cannot express, so a grid would let you draw a permission nothing enforces. Roles against operations is the opposite case: it is exactly what a CEL rule over this context can say. Each column is one expression, and ticking a cell adds one branch to it, so the grid is a picture of the engine rather than a promise beyond it.'],
    ['Rules are one table of five sentences, not a tab per operation.',
     'The question anyone actually has is “what can a technician do with this entity?”, and that is a question about all five operations at once. So the five are one table, each row saying in plain language who is allowed, with the CEL it produced underneath. The editor opens inside the row rather than behind a tab, because comparing a row with the four around it is the whole point — and the simulator answers for all five at once, not one at a time.'],
    ['A condition belongs to the way in, not to the column.',
     'The shape (who || who) && when cannot say “dispatchers always, the assignee only while the job is open” — the commonest rule anyone writes. So each way in carries its own tests: (roleA) || (owner == @user.id && status == ‘scheduled’). A matrix cell that is ticked with a condition says so under the tick. When every branch happens to carry the same test the CEL is emitted factored, because that is the same expression and reads better.'],
    ['What a rule may say, checked against cel.md rather than assumed.',
     'A rule may negate, test presence with has(), use a boolean field bare, compare a field to a literal, to @user.id, to @tenant.id or to another field of the same row, and nest freely. It may NOT call a function: endsWith, contains and matches do not exist, and @user is a closed set of id and roles. So an attribute gate on a mail domain — the rule baas-analyza §16.1 sketches by name — is not expressible in any block. That is a recorded decision (#146, cel.md deviation 1), and examples/complex-crm/NOT-RUNNABLE.md records what it cost that example. The builder now offers everything in the first list and nothing outside it.'],
    ['The builder warns about the null trap rather than letting it bite.',
     'Alvo collapses a comparison with an empty value to false and applies ! afterwards, so a record with no owner PASSES !(owner_id == @user.id). Any “is not” test on a nullable field now carries that sentence at the control.'],
    ['A rule is who OR who, AND when AND when — and both halves are additive.',
     'I first claimed the context could express only two shapes and gave the editor two fixed controls. That was wrong: CEL can also test the record’s own fields, and a real rule does — “dispatchers, or the assigned technician, but only while the job is not completed”. So the editor has two sections that combine differently. Who is a set of alternatives joined by ||: any one admits the caller. When is a set of tests on the record joined by &&: all must hold. Both have an add button, because that is where “add another condition” obviously goes.'],
    ['The simulator answers about a real record, not an abstract one.',
     'Once a rule can test a field, “is this record theirs?” stops being enough — the answer depends on the record’s status too. So the simulator picks a caller and an actual row, and every verdict names the branch that decided it and the value that failed. A toggle could not have said “this record’s status is completed”.'],
    ['The builder reads an expression back, or admits it cannot.',
     'It recognises exactly the two shapes it can write, joined by ||. Anything else sets raw: the controls step aside and the text editor takes over, saying so. That makes the round trip exact or absent, never approximate — a builder that silently rewrites a hand-written rule is worse than no builder. It is also what lets one screen serve every entity, since each one’s model is parsed from its own descriptor.'],
    ['A ref field gets a search picker, and the reverse side gets a subgrid.',
     'A ref is the one type whose value lives in another entity. A dropdown of 1,840 customers is not a control; search over the display field with something to tell two apart is. On the other side, a customer’s jobs are the reverse of that same field — which is also what makes the open_jobs rollup possible.'],
    ['The Schema screen opens with the model drawn, and the drawing is read-only.',
     'Understanding a model someone else wrote is a shape problem, and a list of ref fields is a poor way to see a shape. The map places entities by how deep their references go, draws an arrow from each ref field to what it points at, and puts the onDelete beside it — the one fact about a relation you cannot guess. It is a view: you cannot drag a new relation into existence, because there is no relation object to create.'],
    ['Entities grew an API tab.',
     'Alvo’s output is a REST API, and the first question after modelling is “how do I call this?”. The routes, the rule guarding each one, and a real request and response are all derivable from the schema — nothing is invented, and it was the highest-value screen missing from the first prototype.'],
    ['Entities grew an On write tab for hooks.',
     'before* and after* hooks are honoured in this build and had no UI at all — live behaviour that was invisible. The two kinds are never one list: a before-hook runs in the transaction and can refuse the write; an after-hook runs post-commit and can reach the network.'],
    ['Integrations is its own section.',
     'Endpoints and templates are project-level, not per-entity, and they carry the sharpest honesty problem in the product: reachable from an after-hook, unreachable from an automation rule, and unsigned either way. That needs a page, not a footnote.'],
    ['The assistant is in F5, and it does not need a secret store.',
     'The written design deferred it because a model key needs somewhere safe to live. But an instance-level key arrives the way the connection string already does — Alvo:Ai:ApiKey in configuration. A store is only needed for a per-project key typed into the dashboard, which is a later, separate feature. This is a deviation from the written design, and the reason it is safe to make.'],
    ['The assistant sits where the work is, not only in the sidebar.',
     'Its value is knowing what you were looking at when you asked, so it is reachable from the entity header, from beside \u201cAdd field\u201d as \u201cDescribe it instead\u201d, and from the permissions table \u2014 and the conversation it opens with matches that context: on a schema screen it adds and changes fields, on a rules screen it explains who can do what.'],
    ['The assistant proposes; it never applies.',
     'Its writes arrive as the same diff the schema editor produces and leave through the same dry run and the same Apply button. It has no path to the database that the preview screen does not gate — which is what lets it be useful without being trusted.'],
    ['The first-run wizard does not create an entity.',
     'It would be a second, worse copy of the schema editor, and you meet that editor thirty seconds later anyway. Onboarding ends by handing you to the real tool.'],
    ['Activity is Configuration history.',
     'The drawn feed is a data-level audit log, which does not exist. The descriptor’s revisions are a real, complete audit trail of configuration, and they had no consumer anywhere in the product.'],
  ];

  const rejected = [
    ['Usage charts, request rates, latency graphs', 'Nothing collects them. A dashboard whose headline numbers are invented is worse than one without them.'],
    ['A draggable ERD canvas', 'The map on the Schema screen is a view, not an editor. Alvo has no relation object — a relation is a ref field — so a dragged line would have nowhere to be written. Reading the model is useful; drawing it there is a lie.'],
    ['A permission matrix of TEAMS against entities', '@user exposes id and roles, and nothing else — a team grid would let you draw a permission the engine cannot enforce. Roles against operations is a different question and it is on the Rules screen, because that one the engine can answer.'],
    ['A webhook delivery log with redelivery', 'Deliveries happen only from after-hooks and nothing records them. The log arrives with the engine that makes it worth reading.'],
    ['A live record feed, realtime indicators', 'realtime is unhonoured for every entity of every descriptor.'],
    ['A C# editor for functions', 'No function is ever invoked. A code editor designed before the runtime exists is a guess.'],
  ];

  return `${header([{ label: 'Design notes' }])}
  <div class="a-content"><div class="a-stack" style="max-width:940px">
    <div><h1 class="a-page-title">What this prototype asks for</h1>
      <p class="p-muted p-tight" style="max-width:72ch">Two stylesheets load here. <code class="a-mono">alvo.css</code> is the one already in the repository, unchanged. <code class="a-mono">proposed.css</code> is what this design adds — no new colour, no new token, only new components built from the ones that exist.</p></div>

    <div class="a-panel">
      <div class="a-section"><span class="a-section-title">Components</span></div>
      <table class="a-grid"><thead><tr><th>Class</th><th>What it is</th><th>Status</th></tr></thead>
        <tbody>${components.map(([c, w, s]) => `<tr><td class="a-mono" style="font-size:var(--text-xs)">${c}</td><td>${w}</td>
          <td><span class="a-badge${s === 'new' ? ' a-badge--accent' : ''}">${s === 'new' ? 'to add' : 'in alvo.css'}</span></td></tr>`).join('')}</tbody></table>
    </div>

    <div class="a-panel">
      <div class="a-section"><span class="a-section-title">Decisions</span>
        <span class="a-section-sub">Stated so a later reader can tell a decision from an oversight.</span></div>
      ${decisions.map(([d, why], i) => `<div style="padding:var(--space-4);border-bottom:1px solid var(--border)">
        <div class="a-row" style="align-items:flex-start"><span class="a-badge a-badge--accent" style="flex:none">D${i + 1}</span>
          <span style="font-size:var(--text-sm);font-weight:var(--weight-medium)">${d}</span></div>
        <p class="p-muted p-tight" style="margin-top:var(--space-2);max-width:80ch">${why}</p></div>`).join('')}
    </div>

    <div class="a-panel">
      <div class="a-section"><span class="a-section-title">The field editor changes with the type</span>
        <span class="a-section-sub">Not a design choice \u2014 <code class="a-mono">$defs/field</code> in the frozen schema: nine <code class="a-mono">if/then</code> rules plus <code class="a-mono">additionalProperties: false</code>.</span></div>
      <table class="a-grid">
        <thead><tr><th>Type</th><th>Required by the schema</th><th>Optional</th><th>What the drawer shows</th></tr></thead>
        <tbody>${TYPES.map(([t]) => {
          const m = FACETS[t];
          return `<tr>
            <td class="a-mono" style="color:var(--text)">${t}</td>
            <td>${m.needs.length ? m.needs.map((k) => `<span class="a-badge a-badge--accent">${k}</span>`).join(' ') : '<span class="p-muted">\u2014</span>'}</td>
            <td>${m.optional.length ? m.optional.map((k) => `<span class="a-badge">${k}</span>`).join(' ') : '<span class="p-muted">\u2014</span>'}</td>
            <td class="p-muted">${m.needs.length ? 'a block it will not let you leave empty' : m.optional.length ? 'ordinary fields' : 'nothing \u2014 and that is correct'}</td>
          </tr>`;
        }).join('')}</tbody>
      </table>
      <div class="p-note" style="margin:var(--space-4)"><span class="p-note__tag">three consequences</span>
        <span><strong>One.</strong> The seven types that show nothing are right, not unfinished \u2014 <code class="a-mono">maxLength</code> on an integer is refused at apply.
        <strong>Two.</strong> <code class="a-mono">decimal</code>, <code class="a-mono">enum</code> and <code class="a-mono">ref</code> have no valid descriptor without their settings, so those are a block rather than an optional section.
        <strong>Three.</strong> Changing a type drops whatever the new one may not carry, and on a field with rows behind it that is a column rewrite \u2014 the drawer says so at the control, and the preview then asks for the entity name.</span></div>
      <div class="p-note" style="margin:0 var(--space-4) var(--space-4)"><span class="p-note__tag">also</span>
        <span><code class="a-mono">computed</code> and <code class="a-mono">rollup</code> exclude each other, and either excludes <code class="a-mono">default</code>. A derived field therefore has no constraints panel at all \u2014 nobody writes it, so \u201crequired\u201d and \u201cunique\u201d have nothing to mean.</span></div>
    </div>

    <div class="a-panel">
      <div class="a-section"><span class="a-section-title">Deliberately not in the dashboard</span>
        <span class="a-section-sub">Things a BaaS dashboard usually has, left out because this build cannot honestly serve them.</span></div>
      ${rejected.map(([what, why]) => `<div class="a-row" style="align-items:flex-start;padding:var(--space-3) var(--space-5);border-bottom:1px solid var(--border)">
        <span style="flex:none;width:250px;font-size:var(--text-sm)">${what}</span>
        <span class="p-muted" style="flex:1">${why}</span></div>`).join('')}
    </div>

    <div class="a-panel">
      <div class="a-section"><span class="a-section-title">Still open</span></div>
      ${['Does a technician see this dashboard at all, or only the API? The viewer level exists; nobody has decided whether it appears in sign-in.',
         'Reordering fields changes the descriptor and changes nothing in the database. Worth a control, or noise?',
         'A json field on the record form: a textarea accepts invalid JSON until submit. Worth a real editor?',
         'Bulk actions across 24,680 rows — select-all-matching-filter, or only the loaded page?',
         'Should the assistant read record data by default, or schema and rules only until you turn it on?',
         'Role changes are not audited \u2014 the audit log is #42. Until it lands, should the dashboard refuse to change roles, or change them and say plainly that nothing records it?',
         'A person holding no role at all cannot open the dashboard. Should they be told that at sign-in, or just see nothing?',
         'Hooks are shown and not yet editable here. An editor for them is its own design — does it land in F5 or wait for the automation builder it will resemble?']
        .map((q) => `<div class="a-row" style="align-items:flex-start;padding:var(--space-3) var(--space-5);border-bottom:1px solid var(--border)">
          <span class="p-muted">${q}</span></div>`).join('')}
    </div>
  </div></div>`;
}

/* ==========================================================================
   Overlays
   ========================================================================== */

/* The assistant is a drawer rather than a page because its whole value is
   knowing what you were looking at when you asked. The transcript below is
   chosen by that context: on a schema screen it edits the model, elsewhere it
   answers about access. Either way its writes arrive as the diff component
   the schema editor already uses and leave through the same dry run. */
function aiTranscript(kind) {
  if (kind === 'schema') {
    return `
      <div class="a-ai__turn a-ai__turn--you"><div class="a-ai__bubble">Add the invoice number our accounting system uses</div></div>

      <div class="a-ai__turn">
        <div class="a-ai__bubble">A reference from another system, so: <code class="a-mono">string</code>, unique, and not required \u2014 24,680 rows already exist without one and a required column would refuse every one of them. I left the length at 32; accounting references are rarely longer.</div>
        <div class="a-ai__sources">read: GET /management/schema \u00b7 GET /api/work_orders?limit=1</div>
        <div class="a-ai__proposal">
          <span class="a-row"><span class="a-badge a-badge--accent">proposed change</span>
            <span class="p-muted">1 field on work_orders</span></span>
          ${diffBlock([
            ['ctx', '"fields": {'], ['ctx', '  \u2026'],
            ['add', '  "invoice_ref": {'],
            ['add', '    "type": "string",'],
            ['add', '    "unique": true,'],
            ['add', '    "maxLength": 32,'],
            ['add', '    "description": "Its reference in accounting."'],
            ['add', '  }'],
            ['ctx', '}'],
          ])}
          <div class="a-row">
            <button class="a-btn a-btn--sm a-btn--ghost">Make it required instead</button>
            <span style="margin-left:auto" class="p-hstack">
              <button class="a-btn a-btn--sm">Open in the field editor</button>
              <button class="a-btn a-btn--sm a-btn--primary" data-act="go" data-route="#/schema/preview">Review as a change</button></span>
          </div>
        </div>
      </div>

      <div class="a-ai__turn a-ai__turn--you"><div class="a-ai__bubble">quoted_price should hold larger numbers</div></div>

      <div class="a-ai__turn">
        <div class="a-ai__bubble">It is <code class="a-mono">decimal(10,2)</code>, so it stops at 99,999,999.99. Widening the total digits to 12 is safe \u2014 no stored value loses precision, and Postgres rewrites the column without a table scan. Narrowing it later would not be, so this is a one-way move in practice.</div>
        <div class="a-ai__sources">read: GET /management/schema \u00b7 PUT /management/descriptor?dryRun=true</div>
        <div class="a-ai__proposal">
          <span class="a-row"><span class="a-badge a-badge--accent">proposed change</span>
            <span class="p-muted">checked against your database</span></span>
          ${diffBlock([
            ['ctx', '"quoted_price": {'], ['ctx', '  "type": "decimal",'],
            ['del', '  "precision": 10,'], ['add', '  "precision": 12,'],
            ['ctx', '  "scale": 2'], ['ctx', '}'],
          ])}
          <span class="p-muted">The dry run came back clean: ALTER COLUMN, no lock worth naming.</span>
          <div class="a-row"><span style="margin-left:auto" class="p-hstack">
            <button class="a-btn a-btn--sm a-btn--primary" data-act="go" data-route="#/schema/preview">Review as a change</button></span></div>
        </div>
        <span class="p-muted" style="font-size:var(--text-2xs)">Nothing is applied. Every change it proposes lands in the same preview a hand edit lands in.</span>
      </div>`;
  }

  return `
    <div class="a-ai__turn a-ai__turn--you"><div class="a-ai__bubble">Why can Peter not see WO-100419?</div></div>

    <div class="a-ai__turn">
      <div class="a-ai__bubble">Peter holds only <code class="a-mono">technician</code>. The <code class="a-mono">get</code> rule on <code class="a-mono">work_orders</code> admits a technician only when <code class="a-mono">assigned_to == @user.id</code>, and WO-100419 is assigned to Martin. So the record is filtered out of his list rather than refused \u2014 he sees a shorter list, not an error.</div>
      <div class="a-ai__sources">read: GET /management/schema \u00b7 POST /management/policy/simulate</div>
    </div>

    <div class="a-ai__turn a-ai__turn--you"><div class="a-ai__bubble">Let technicians in the same region see it too</div></div>

    <div class="a-ai__turn">
      <div class="a-ai__bubble">That needs the caller's region, and a rule can only reach <code class="a-mono">@user.id</code>, <code class="a-mono">@user.roles</code> and this entity's own fields \u2014 there is no <code class="a-mono">@user.region</code> to compare against. Two ways round it. The cheap one is a role per region:</div>
      <div class="a-ai__proposal">
        <span class="a-row"><span class="a-badge a-badge--accent">proposed change</span>
          <span class="p-muted">1 rule, 4 new roles</span></span>
        ${diffBlock([
          ['ctx', '"get":'],
          ['del', "  \"'dispatcher' in @user.roles"],
          ['del', '   || assigned_to == @user.id"'],
          ['add', "  \"'dispatcher' in @user.roles"],
          ['add', '   || assigned_to == @user.id'],
          ['add', "   || ('tech-' + region_id) in @user.roles\""],
        ])}
        <span class="p-muted">It costs a role per region and it is enforced inside the query rather than after it. The other way \u2014 a region on the user \u2014 needs the CEL context to widen, which is a framework change, not a descriptor one.</span>
        <div class="a-row">
          <button class="a-btn a-btn--sm a-btn--ghost">Explain the other way</button>
          <span style="margin-left:auto" class="p-hstack">
            <button class="a-btn a-btn--sm" data-act="go" data-route="#/rules">Try it in the simulator</button>
            <button class="a-btn a-btn--sm a-btn--primary" data-act="go" data-route="#/schema/preview">Review as a change</button></span>
        </div>
      </div>
      <span class="p-muted" style="font-size:var(--text-2xs)">Nothing is applied. \u201cReview as a change\u201d opens the same preview a hand edit opens, with the same dry run against your database.</span>
    </div>`;
}

function aiDrawer() {
  const onSchema = state.route.startsWith('#/schema/');
  const where = onSchema ? `schema \u00b7 ${state.entity}`
    : state.route.startsWith('#/data/') ? `data \u00b7 ${state.entity}`
    : state.route.startsWith('#/rules') ? `rules \u00b7 ${state.route.split('/')[2] || 'work_orders'}`
    : (state.route.replace('#/', '') || 'overview');

  const suggestions = onSchema
    ? ['Add a field for the site contact', 'Should scheduled_for be indexed?', 'Split the address into its own entity']
    : ['Add an invoices entity', 'Why was my last apply refused?', 'Which fields have no index?'];

  return `<div class="a-ai">
    <div class="a-ai__head">${icon('spark')}<span class="a-section-title">Ask Alvo</span>
      <button class="a-btn a-btn--sm a-btn--ghost" style="margin-left:auto" data-act="close">Close</button></div>
    <div class="a-ai__context">you are on <code class="a-mono">${where}</code> \u00b7 it reads schema, rules and capabilities \u00b7 it cannot apply anything</div>

    <div class="a-ai__log">${aiTranscript(onSchema ? 'schema' : 'rules')}</div>

    <div class="a-ai__composer">
      <div class="a-ai__suggest">${suggestions.map((s) => `<button class="a-preset">${s}</button>`).join('')}</div>
      <div class="a-row">
        <input class="a-input" placeholder="${onSchema ? `Describe a change to ${state.entity}\u2026` : 'Ask about this project\u2026'}" aria-label="Ask Alvo">
        <button class="a-btn a-btn--primary">Send</button>
      </div>
    </div>
  </div>`;
}

function overlay() {
  if (state.ai) return `<div class="p-overlay p-overlay--right"><div class="a-scrim" data-act="close"></div><div class="p-overlay__panel">${aiDrawer()}</div></div>`;
  if (!state.overlay) return '';
  const { kind, id } = state.overlay;
  const wrap = (pos, panel) => `<div class="p-overlay p-overlay--${pos}"><div class="a-scrim" data-act="close"></div><div class="p-overlay__panel">${panel}</div></div>`;

  if (kind === 'palette') {
    return wrap('top', `<div class="a-palette">
      <input class="a-palette__input" placeholder="Jump to a screen, or ask a question" autofocus>
      ${PALETTE_ITEMS.map((p, i) => `<div class="a-palette__item${i === 0 ? ' a-palette__item--active' : ''}" data-act="${p.ai ? 'ai' : 'go'}" data-route="${p.route}">
        ${icon(p.ai ? 'spark' : 'search')}<span>${p.label}</span><span class="a-kbd" style="margin-left:auto">${p.hint}</span></div>`).join('')}
    </div>`);
  }

  if (kind === 'person') return wrap('right', personDrawer(id));
  if (kind === 'field') return wrap('right', fieldDrawer(id));
  if (kind === 'record') return wrap('right', recordDrawer(id));
  if (kind === 'record-new') return wrap('right', recordForm());

  if (kind === 'export') {
    return wrap('center', `<div class="a-modal"><div class="a-stack">
      <div><span class="a-section-title">Export field-service</span>
        <p class="p-muted p-tight">Revision 7, exactly as applied.</p></div>
      <pre class="a-json p-scroll" style="max-height:300px">${descriptorJson(entity('work_orders'), null)}</pre>
      <div class="a-row"><button class="a-btn a-btn--ghost" data-act="close">Close</button>
        <span style="margin-left:auto" class="p-hstack"><button class="a-btn">Copy</button>
        <button class="a-btn a-btn--primary">Download .json</button></span></div></div></div>`);
  }

  if (kind === 'new-key') {
    return wrap('center', `<div class="a-modal"><div class="a-stack">
      <div><span class="a-section-title">dispatch-integration is ready</span>
        <p class="p-muted p-tight">Copy it now. Alvo stores a hash, so this is the only time the key is readable.</p></div>
      <div class="a-reveal"><span class="a-reveal__value">alvo_sk_7FqR2mX9vK4pLdN8wZ3jH6bQ1sT5yG0c</span>
        <button class="a-btn a-btn--sm">Copy</button></div>
      <div class="a-row"><button class="a-btn a-btn--primary" style="margin-left:auto" data-act="close">I have saved it</button></div></div></div>`);
  }

  if (kind === 'rollback' || kind === 'delete-project' || kind === 'bulk-delete') {
    const copy = {
      rollback: [`Restore revision ${id}`, `Revision ${id} is older than the current schema. Restoring it drops the columns added since — including access_code, with its values, on 24,680 rows.`, 'field-service', `Restore r${id}`],
      'delete-project': ['Delete field-service', 'Every table, every record and every revision. 26,532 records. Nothing can be exported afterwards.', 'field-service', 'Delete this project'],
      'bulk-delete': ['Delete 3 work orders', 'Two are referenced by nothing. One is in progress and assigned to a technician who is on site now.', 'delete 3', 'Delete them'],
    }[kind];
    return wrap('center', `<div class="a-modal"><div class="a-stack">
      <div><span class="a-section-title">${copy[0]}</span><p class="p-muted p-tight">${copy[1]}</p></div>
      <div class="a-confirm">
        <span style="font-size:var(--text-xs);color:var(--danger-fg);font-weight:var(--weight-medium)">Type <code class="a-mono" style="color:var(--danger-fg)">${copy[2]}</code> to confirm</span>
        <input class="a-input" placeholder="${copy[2]}" aria-label="Confirmation"></div>
      <div class="a-row"><button class="a-btn a-btn--ghost" data-act="close">Cancel</button>
        <button class="a-btn a-btn--danger" style="margin-left:auto" aria-disabled="true">${copy[3]}</button></div></div></div>`);
  }

  if (kind === 'assign') {
    const u = USERS.find((x) => x.email === id);
    const available = [...catalog(), ...BUILTIN.filter((r) => r !== 'anon')].filter((r) => !assignedTo(u).includes(r));
    return wrap('center', `<div class="a-modal"><div class="a-stack">
      <div><span class="a-section-title">Give ${esc(u.name)} a role</span>
        <p class="p-muted p-tight">Takes effect on their next request. It does not change the descriptor.</p></div>
      ${available.length ? `<div class="p-hstack">${available.map((r) => `<button class="a-preset" data-act="assign" data-email="${u.email}" data-role="${r}" type="button">${r}</button>`).join('')}</div>`
        : '<span class="p-muted">They already hold every role this project declares.</span>'}
      <span class="p-muted"><code class="a-mono">anon</code> cannot be given to anybody \u2014 it means the absence of an identity.</span>
      <div class="a-row"><button class="a-btn a-btn--ghost" style="margin-left:auto" data-act="close">Cancel</button></div>
    </div></div>`);
  }

  if (kind === 'new-role') {
    return wrap('center', `<div class="a-modal"><div class="a-stack a-form">
      <div><span class="a-section-title">New role</span>
        <p class="p-muted p-tight">A role is a name you can hand to people and then name in a rule. It carries no permissions of its own \u2014 what it may do is decided per entity, in Rules.</p></div>
      <div class="a-field"><span class="a-label">Name<span class="a-label__hint">Lower case, no spaces. <code class="a-mono">anon</code>, <code class="a-mono">authenticated</code> and <code class="a-mono">admin</code> already exist and cannot be redeclared.</span></span>
        <input class="a-input" id="nr-name" placeholder="billing-manager" style="font-family:var(--font-mono)" autofocus></div>
      <div class="p-note"><span class="p-note__tag">next</span>
        <span>Adding it changes the descriptor, so it appears in the pending bar and needs an apply. Until then you can assign it to people, and it will match nothing.</span></div>
      <div class="a-row"><button class="a-btn a-btn--ghost" data-act="close">Cancel</button>
        <button class="a-btn a-btn--primary" style="margin-left:auto" data-act="addrole">Add to the descriptor</button></div>
    </div></div>`);
  }

  if (kind === 'new-entity') {
    return wrap('center', `<div class="a-modal"><div class="a-stack a-form">
      <div><span class="a-section-title">New entity</span>
        <p class="p-muted p-tight">Name it in the plural, the way you would say it out loud.</p></div>
      <div class="a-field"><span class="a-label">Name<span class="a-label__hint">Alvo adds id, created_at and updated_at itself.</span></span>
        <input class="a-input" id="ne-name" placeholder="invoices" autofocus></div>
      <div class="a-field"><span class="a-label">Who sees the records<span class="a-label__hint">Hard to change later — it decides whether every row carries a tenant.</span></span>
        <div class="p-hstack">
          <button class="a-preset a-preset--on">Each customer sees only their own</button>
          <button class="a-preset">Everyone sees the same rows</button></div></div>
      <label class="a-row" style="gap:var(--space-3)"><span class="a-toggle" role="switch" aria-checked="false"></span>
        <span><span style="font-size:var(--text-sm)">Keep a version on every row</span>
        <span class="a-switcher-meta">lets a write be made conditional, and mints an ETag</span></span></label>
      <div class="a-row"><button class="a-btn a-btn--ghost" data-act="close">Cancel</button>
        <button class="a-btn a-btn--primary" style="margin-left:auto" data-act="close">Create entity</button></div></div></div>`);
  }

  if (kind === 'projects') {
    return wrap('top', `<div class="a-palette">
      <div class="a-palette__item a-palette__item--active">${avatar('F')}field-service<span class="a-badge a-badge--ok" style="margin-left:auto">current</span></div>
      <div class="a-palette__item"><span class="p-muted">One project per instance. A second needs its own connection and its own migration history.</span></div>
    </div>`);
  }

  return '';
}

/* --- Field drawer -------------------------------------------------------- */

function fieldDrawer(name) {
  const e = entity(state.entity) || entity('work_orders');
  const isNew = name === '__new';
  const f = isNew ? { name: '', type: 'string' } : e.fields.find((x) => x.name === name) || e.fields[0];
  const derived = !!(f.computed || f.rollup);

  const chosen = TYPES.find(([t]) => t === f.type) || TYPES[0];
  const types = `<div class="a-typegrid">${TYPES.map(([t]) => `<button class="a-typechip${t === f.type ? ' a-typechip--on' : ''}" data-act="noop" type="button">${t}</button>`).join('')}</div>
    <span class="a-typehint">${esc(chosen[1])}</span>`;

  /* One branch per type, and the seven that render nothing render nothing on
     purpose: the schema forbids any type-specific setting on them. The three
     types whose settings are REQUIRED get a block that says so, because a
     decimal with no precision is not an incomplete form — it is a descriptor
     the apply refuses. */
  const needed = (body) => `<div class="a-needed">
      <span class="a-needed__head">${icon('check')} Needed for ${/^[aeiou]/.test(f.type) ? 'an' : 'a'} ${f.type}</span>${body}</div>`;

  const facet = () => {
    if (f.type === 'string') return `<div class="a-field"><span class="a-label">Longest value allowed<span class="a-label__hint">Becomes the column width. Widening one later is safe; narrowing it is not.</span></span>
        <input class="a-input" value="${f.maxLength || ''}" placeholder="160"></div>
      <div class="a-field"><span class="a-label">Must look like<span class="a-label__hint">A named format is a pattern Alvo anchors over the whole value \u2014 a value with trailing text is refused by the framework, not by you.</span></span>
        <div class="p-hstack">${['none', 'email', 'phone', 'url', 'work-order-ref'].map((o) => `<button class="a-preset${(f.format || 'none') === o ? ' a-preset--on' : ''}" type="button">${o}</button>`).join('')}</div></div>`;

    if (f.type === 'decimal') return needed(`
      <div class="a-row" style="align-items:flex-start;gap:var(--space-4)">
        <div class="a-field" style="flex:1"><span class="a-label">Total digits</span><input class="a-input" value="${f.precision || 10}"></div>
        <div class="a-field" style="flex:1"><span class="a-label">After the point</span><input class="a-input" value="${f.scale || 2}"></div></div>
      <span class="p-muted">Total counts every digit, not only the ones after the point \u2014 <code class="a-mono">10,2</code> holds up to 99,999,999.99. Both are required: a decimal without them cannot be applied.</span>`);

    if (f.type === 'enum') return needed(`
      <div class="a-field"><span class="a-label">Allowed values</span>
        <div class="p-hstack">${(f.values || []).map((v) => `<span class="a-chip">${v} <span aria-hidden="true">\u2715</span></span>`).join('')}
        <span class="a-chip" style="border-style:dashed">+ Add value</span></div></div>
      <span class="p-muted">At least one is required. Removing a value is refused while a record still holds it \u2014 the check runs at apply, against your data.</span>`);

    if (f.type === 'ref') return needed(`
      <div class="a-field"><span class="a-label">Points at</span>
        <div class="p-hstack">${ENTITIES.filter((x) => x.name !== e.name).map((x) => `<button class="a-preset${x.name === f.entity ? ' a-preset--on' : ''}" type="button">${x.name}</button>`).join('')}</div></div>
      <div class="a-field"><span class="a-label">When that record is deleted<span class="a-label__hint">Optional. Without it the delete is refused, which is the safe default.</span></span>
        <div class="p-hstack">
          <button class="a-preset${f.onDelete === 'restrict' ? ' a-preset--on' : ''}" type="button">Refuse the delete</button>
          <button class="a-preset${f.onDelete === 'setNull' ? ' a-preset--on' : ''}" type="button">Clear this field</button>
          <button class="a-preset${f.onDelete === 'cascade' ? ' a-preset--on' : ''}" type="button">Delete this record too</button></div></div>`);

    return '';
  };

  /* What a type change would cost. Said here rather than at the preview,
     because the click that causes it happens here. */
  const held = FACETS[f.type] ? [...FACETS[f.type].needs, ...FACETS[f.type].optional].filter((k) => f[k] !== undefined) : [];
  const typeWarn = !isNew && held.length
    ? `<div class="a-typewarn">\u26a0 <span>Changing the type drops <code class="a-mono">${held.join('</code>, <code class="a-mono">')}</code> \u2014 the schema allows ${held.length > 1 ? 'those' : 'that'} only on <code class="a-mono">${f.type}</code>. On a field with ${e.rows.toLocaleString('en-US')} rows behind it, the column is rewritten, and the preview will ask you to type the entity name.</span></div>`
    : '';

  return `<div class="a-drawer a-drawer--wide"><div class="a-form">
    <div class="p-between">
      <div><span class="a-page-title" style="font-size:var(--text-lg)">${isNew ? 'New field' : f.name}</span>
        <p class="p-muted p-tight">on <code class="a-mono">${e.name}</code></p></div>
      <button class="a-btn a-btn--sm a-btn--ghost" data-act="close">Close</button>
    </div>

    <div class="a-field"><span class="a-label">Field name<span class="a-label__hint">Renaming keeps the data — Alvo writes <code class="a-mono">renamedFrom</code> and the migration moves the column.</span></span>
      <input class="a-input" value="${f.name}" placeholder="scheduled_for" style="font-family:var(--font-mono)"></div>

    <div class="a-field"><span class="a-label">Type</span>${types}${typeWarn}</div>

    ${f.description ? `<div class="a-field"><span class="a-label">What it holds<span class="a-label__hint">Becomes this field’s description in the generated OpenAPI document.</span></span>
      <textarea class="a-textarea" style="min-height:64px">${esc(f.description)}</textarea></div>` : ''}

    ${facet()}

    ${derived ? `<div class="a-field"><span class="a-label">Alvo maintains this value<span class="a-label__hint">Nobody writes it through the API, and it is kept in the same transaction as the change that moves it.</span></span>
        ${f.computed ? `<div class="a-readout"><span class="a-readout__tag">from this row</span><span>${esc(f.computed)}</span></div>`
          : `<div class="a-readout"><span class="a-readout__tag">over children</span><span>${f.rollup.op}(${f.rollup.from}${f.rollup.field ? '.' + f.rollup.field : ''})</span></div>`}
      </div>`
      : `<div class="a-panel" style="padding:var(--space-4);display:flex;flex-direction:column;gap:var(--space-4)">
      ${[['Must have a value', 'required', f.required],
         ['No two records share it', 'unique', f.unique],
         ['Alvo maintains it, callers cannot write it', 'readOnly', f.readOnly],
         ['Never appears in a response or in the schema', 'hidden', f.hidden],
         ['Indexed on its own, for filtering and sorting', 'index', f.index]].map(([label, k, on]) => `
        <label class="a-row" style="gap:var(--space-3)">
          <span class="a-toggle${on ? ' a-toggle--on' : ''}" role="switch" aria-checked="${!!on}"></span>
          <span><span style="font-size:var(--text-sm)">${label}</span><span class="a-switcher-meta">${k}</span></span></label>`).join('')}
    </div>`}

    <details class="a-disclose">
      <summary>This field in the descriptor</summary>
      <div class="a-disclose__body">
        <pre class="a-json" style="max-height:220px">${highlight(fieldJson(f))}</pre>
        <div class="a-row" style="margin-top:var(--space-3)">
          <button class="a-btn a-btn--sm a-btn--ghost">Copy</button>
          <button class="a-btn a-btn--sm a-btn--ghost">Show as OpenAPI</button></div>
      </div>
    </details>

    <details class="a-disclose">
      <summary>Two constraints this build cannot enforce</summary>
      <div class="a-disclose__body" style="display:flex;flex-direction:column;gap:var(--space-4)">
        ${['field.default', 'field.validation'].map((k) => `<div>
          <div class="a-refused">
            <span class="a-label">${REFUSED[k].label}</span>
            <input class="a-input" placeholder="${k.endsWith('default') ? 'scheduled' : 'value.matches(…)'}" disabled></div>
          <div class="a-refused__reason">⚠ <span>${esc(REFUSED[k].consequence)}</span></div>
          <div class="a-refused__reason" style="color:var(--dim)"><span>→</span> <span>${esc(REFUSED[k].fix)}</span></div>
        </div>`).join('')}
        <span class="p-muted">Refused at apply rather than ignored, so a descriptor declaring one is rejected instead of quietly storing the wrong value. The control is here, and inert, so you can see the decision was made rather than forgotten.</span>
      </div>
    </details>

    <div class="a-row" style="position:sticky;bottom:0;background:var(--panel);padding-top:var(--space-3)">
      ${isNew ? '' : '<button class="a-btn a-btn--danger a-btn--sm">Remove field</button>'}
      <span style="margin-left:auto" class="p-hstack">
        <button class="a-btn a-btn--ghost" data-act="close">Cancel</button>
        <button class="a-btn a-btn--primary" data-act="close">${isNew ? 'Add to the model' : 'Update the model'}</button></span>
    </div>
    <span class="p-muted">Nothing is applied yet — this edits the descriptor beside the editor.</span>
  </div></div>`;
}

/* --- Record drawer -------------------------------------------------------- */

function recordDrawer(id) {
  const r = WORK_ORDERS.find((x) => x.id === id) || WORK_ORDERS[0];
  const siblings = WORK_ORDERS.filter((x) => x.customerId === r.customerId);
  const cust = CUSTOMERS.find((c) => c.id === r.customerId) || CUSTOMERS[0];

  return `<div class="a-drawer a-drawer--wide"><div class="a-stack">
    <div class="p-between">
      <div><span class="a-page-title" style="font-size:var(--text-lg)">${esc(r.title)}</span>
        <p class="a-mono p-tight">${r.reference} · ${r.id}</p></div>
      <button class="a-btn a-btn--sm a-btn--ghost" data-act="close">Close</button>
    </div>

    <div class="p-hstack">${statusBadge(r.status)}${r.emergency ? '<span class="a-badge a-badge--danger">emergency</span>' : ''}
      <span class="a-badge">priority ${r.priority}</span><span class="a-badge">version 4</span></div>

    <dl class="p-kv">
      <dt>quoted_price</dt><dd style="font-variant-numeric:tabular-nums">${eur(r.quoted_price)}</dd>
      <dt>tax</dt><dd style="font-variant-numeric:tabular-nums">${eur(r.quoted_price * 0.23)} <span class="a-badge a-badge--accent">computed</span></dd>
      <dt>scheduled_for</dt><dd>${r.scheduled_for || '— not scheduled'}</dd>
      <dt>assigned_to</dt><dd>${esc(r.owner)}</dd>
      <dt>region_id</dt><dd>${r.region}</dd>
      <dt>external_ref</dt><dd class="p-muted">maintained by the integration</dd>
    </dl>

    <div class="a-field">
      <span class="a-label">customer_id → customers</span>
      <div class="a-picker__chosen">
        ${avatar(cust.name.slice(0, 1))}
        <span style="flex:1"><span style="font-weight:var(--weight-medium)">${esc(cust.name)}</span>
          <span class="a-switcher-meta">${cust.id} · ${cust.tier} tier · ${cust.open_jobs} open jobs</span></span>
        <a class="a-btn a-btn--sm" href="#/data/customers">Open</a>
      </div>
    </div>

    <div class="a-field">
      <span class="a-label">Other jobs for this customer<span class="a-label__hint">The reverse of the same ref field. Nothing extra is declared to get this list.</span></span>
      <div class="a-subgrid">
        <div class="a-subgrid__head"><span>${siblings.length} work orders</span>
          <a class="a-btn a-btn--sm a-btn--ghost" style="margin-left:auto" href="#/data/work_orders">Open in Data</a></div>
        ${siblings.map((s) => `<div class="a-subgrid__row" data-act="overlay" data-kind="record" data-id="${s.id}">
          <span class="a-mono">${s.reference}</span>
          <span style="flex:1;min-width:0;overflow:hidden;text-overflow:ellipsis;white-space:nowrap">${esc(s.title)}</span>
          ${statusBadge(s.status)}</div>`).join('')}
      </div>
    </div>

    <div class="a-panel" style="padding:var(--space-4)">
      <span class="a-label">Two fields are not shown</span>
      <p class="p-muted p-tight" style="margin-top:var(--space-2)"><code class="a-mono">internal_notes</code> and <code class="a-mono">access_code</code> are hidden. They are not withheld from you in particular — they are in no response Alvo sends, to anyone.</p>
    </div>

    <div class="a-row">
      <button class="a-btn a-btn--danger a-btn--sm">Delete</button>
      <span style="margin-left:auto" class="p-hstack">
        <button class="a-btn a-btn--sm" data-act="ai">${icon('spark')} Ask</button>
        <button class="a-btn a-btn--primary a-btn--sm" data-act="overlay" data-kind="record-new">Edit</button></span>
    </div>
  </div></div>`;
}

/* --- Record form ---------------------------------------------------------- */

function recordForm() {
  const e = entity('work_orders');
  const writable = e.fields.filter((f) => !f.readOnly && !f.computed && !f.rollup && f.name !== 'internal_notes');

  const control = (f) => {
    if (f.type === 'ref') return refPicker(f);
    if (f.type === 'enum') return `<div class="p-hstack">${f.values.map((v, i) => `<button class="a-preset${i === 0 ? ' a-preset--on' : ''}" type="button">${v.replace('_', ' ')}</button>`).join('')}</div>`;
    if (f.type === 'text') return '<textarea class="a-textarea"></textarea>';
    if (f.type === 'boolean') return '<span class="a-toggle" role="switch" aria-checked="false"></span>';
    if (f.type === 'json') return '<textarea class="a-textarea" style="font-family:var(--font-mono);font-size:var(--text-xs)" placeholder="{ }"></textarea>';
    if (f.type === 'datetime' || f.type === 'date') return `<input class="a-input" type="${f.type === 'date' ? 'date' : 'datetime-local'}">`;
    if (f.type === 'uuid') return '<select class="a-select"><option>Choose a technician…</option><option>Peter Horváth</option><option>Zuzana Malá</option></select>';
    return `<input class="a-input" placeholder="${f.format === 'work-order-ref' ? 'WO-100427' : ''}">`;
  };

  return `<div class="a-drawer a-drawer--wide"><div class="a-form">
    <div class="p-between">
      <div><span class="a-page-title" style="font-size:var(--text-lg)">New work order</span>
        <p class="p-muted p-tight">Generated from the field types. Add a field in Schema and it appears here.</p></div>
      <button class="a-btn a-btn--sm a-btn--ghost" data-act="close">Close</button>
    </div>

    ${writable.map((f) => `<div class="a-field">
      <span class="a-label">${f.name}${f.required ? ' <span style="color:var(--accent)">∗</span>' : ''}
        <span style="font-weight:var(--weight-normal);color:var(--faint)"> · ${typeLabel(f)}</span>
        ${f.hidden ? '<span class="a-label__hint">Write only. You will not be able to read this back.</span>' : ''}
        ${f.format ? `<span class="a-label__hint">must match ${f.format}</span>` : ''}</span>
      ${control(f)}
    </div>`).join('')}

    <div class="a-error">
      <span class="a-error__title">reference is already taken</span>
      <span class="a-error__detail">WO-100418 belongs to another work order. reference is unique across the instance.</span>
      <span class="a-error__fix">Use the next free number, WO-100427, or reopen the existing job.</span>
      <span class="a-error__type">https://alvo.dev/problems/unique-violation</span>
    </div>

    <div class="a-row" style="position:sticky;bottom:0;background:var(--panel);padding-top:var(--space-3)">
      <button class="a-btn a-btn--ghost" data-act="close">Cancel</button>
      <span style="margin-left:auto" class="p-hstack">
        <span class="p-muted">POST /api/work_orders</span>
        <button class="a-btn a-btn--primary" data-act="close">Create work order</button></span>
    </div>
  </div></div>`;
}

function refPicker(f) {
  if (state.pickerChosen) {
    const c = CUSTOMERS.find((x) => x.id === state.pickerChosen);
    return `<div class="a-picker__chosen">${avatar(c.name.slice(0, 1))}
      <span style="flex:1"><span style="font-weight:var(--weight-medium)">${esc(c.name)}</span>
        <span class="a-switcher-meta">${c.id} · ${c.tier} · ${c.open_jobs} open jobs</span></span>
      <button class="a-btn a-btn--sm a-btn--ghost" data-act="pickclear">Change</button></div>`;
  }
  return `<div class="a-picker">
    <div class="a-picker__field">${icon('search')}
      <input class="a-input" style="border:none;padding:0;background:transparent" placeholder="Search ${f.entity} by name" aria-label="Search ${f.entity}">
      <span class="p-muted" style="white-space:nowrap">1,840 records</span></div>
    <div class="a-picker__list">
      ${CUSTOMERS.slice(0, 4).map((c, i) => `<button class="a-picker__item${i === 0 ? ' a-picker__item--on' : ''}" data-act="pick-ref" data-id="${c.id}" type="button">
        ${avatar(c.name.slice(0, 1))}
        <span><span style="font-weight:var(--weight-medium)">${esc(c.name)}</span>
          <span class="a-switcher-meta">${esc(c.email)}</span></span>
        <span class="a-picker__meta">${c.tier}</span></button>`).join('')}
      <button class="a-picker__item" style="color:var(--accent)" type="button">${icon('plus')} Create a new customer</button>
    </div>
    <div class="a-picker__field" style="border-top:1px solid var(--border)">
      <span class="p-muted">Searches the display field. A dropdown of 1,840 rows would not be a control.</span></div>
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
  '#/rules': () => screenRules('work_orders'),
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

  if (r.startsWith('#/welcome')) {
    app.innerHTML = `<div class="p-frame">${screenWelcome()}</div>${overlay()}`;
    return;
  }

  let screen;
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

  app.innerHTML = `<div class="a-shell p-frame">${sidebar()}<div class="a-main">${screen}${bottomnav()}</div></div>${overlay()}`;
  const hit = $('.a-json__hit');
  if (hit) hit.scrollIntoView({ block: 'center' });
}

/* ==========================================================================
   Events
   ========================================================================== */

document.addEventListener('click', (ev) => {
  const el = ev.target.closest('[data-act]');
  if (!el) return;
  const act = el.dataset.act;

  if (act === 'noop') { ev.preventDefault(); return; }
  if (act === 'go') { ev.preventDefault(); state.overlay = null; state.ai = false; location.hash = el.dataset.route; return; }
  if (act === 'close') { state.overlay = null; state.ai = false; render(); return; }
  if (act === 'ai') { ev.preventDefault(); state.overlay = null; state.ai = true; render(); return; }
  if (act === 'overlay') { state.ai = false; state.overlay = { kind: el.dataset.kind, id: el.dataset.id }; render(); return; }
  if (act === 'tab') { state.tab = el.dataset.tab; render(); return; }
  if (act === 'field') { state.selectedField = el.dataset.field === '__new' ? null : el.dataset.field; state.overlay = { kind: 'field', id: el.dataset.field }; render(); return; }
  if (act === 'state') { state.screenState = el.dataset.state; render(); return; }
  if (act === 'tenant') { state.tenant = el.dataset.id; render(); return; }
  if (act === 'discard') { state.pending = 0; render(); return; }
  if (act === 'apply') { state.pending = 0; location.hash = '#/history'; return; }
  if (act === 'clear') { state.selectedRows.clear(); render(); return; }
  if (act === 'compare') { state.compareB = Number(el.dataset.rev); render(); return; }
  if (act === 'person') { state.overlay = { kind: 'person', id: el.dataset.email }; render(); return; }
  if (act === 'unassign') {
    const list = state.assigned[el.dataset.email];
    const i = list.indexOf(el.dataset.role);
    if (i >= 0) list.splice(i, 1);
    render(); return;
  }
  if (act === 'assign') {
    const list = state.assigned[el.dataset.email];
    if (!list.includes(el.dataset.role)) list.push(el.dataset.role);
    state.overlay = null; render(); return;
  }
  if (act === 'addrole') {
    const name = ($('#nr-name')?.value || '').trim();
    if (name) catalog().push(name);
    state.overlay = null; render(); return;
  }
  if (act === 'delrole') {
    const i = catalog().indexOf(el.dataset.role);
    if (i >= 0) catalog().splice(i, 1);
    render(); return;
  }
  if (act === 'rolereset') { state.roleCatalog = [...PROJECT.roles]; render(); return; }
  if (act === 'rulereset') { delete state.rules[el.dataset.entity]; state.ruleOpen = null; render(); return; }
  if (act === 'ruleopen') {
    const op = el.dataset.op;
    state.ruleOpen = state.ruleOpen === op ? null : op;
    render();
    if (state.ruleOpen) $(`#rule-${op}`)?.scrollIntoView({ block: 'center', behavior: 'smooth' });
    return;
  }
  if (act === 'rulerole' || act === 'ruleowner') {
    const m = rulesFor(el.dataset.entity)[el.dataset.op];
    const who = act === 'rulerole' ? { kind: 'role', role: el.dataset.role } : { kind: 'owner', field: el.dataset.field };
    m.branches = m.branches || [];
    const b = branchFor(m, who);
    if (b) m.branches.splice(m.branches.indexOf(b), 1);
    else m.branches.push({ ...who, conds: [] });
    render(); return;
  }
  if (act === 'ruleraw') {
    const ent = entity(el.dataset.entity);
    const m = rulesFor(el.dataset.entity)[el.dataset.op];
    m.raw = m.raw === null ? celOf(m, ent) : null;
    render(); return;
  }
  if (act === 'condadd') {
    const ent = entity(el.dataset.entity);
    const options = testable(ent);
    const f = options.find((x) => x.type === 'enum') || options.find((x) => x.type === 'boolean') || options[0];
    const b = rulesFor(el.dataset.entity)[el.dataset.op].branches[Number(el.dataset.b)];
    b.conds = [...(b.conds || []), newCond(ent, f)];
    render(); return;
  }
  if (act === 'condremove') {
    rulesFor(el.dataset.entity)[el.dataset.op].branches[Number(el.dataset.b)].conds.splice(Number(el.dataset.i), 1);
    render(); return;
  }
  if (act === 'pick-ref') { state.pickerChosen = el.dataset.id; render(); return; }
  if (act === 'pickclear') { state.pickerChosen = null; render(); return; }
  if (act === 'pick') {
    const id = el.dataset.id;
    if (state.selectedRows.has(id)) state.selectedRows.delete(id); else state.selectedRows.add(id);
    render(); return;
  }
  if (act === 'sim') { state.simulate[el.dataset.k] = el.dataset.v; render(); return; }
});

document.addEventListener('change', (ev) => {
  const el = ev.target.closest('[data-act]');
  if (!el) return;
  const act = el.dataset.act;
  if (act === 'simrec') { state.simulate.record = el.value; render(); return; }
  if (act === 'condfield' || act === 'condop' || act === 'condvalue') {
    const ent = entity(el.dataset.entity);
    const b = rulesFor(el.dataset.entity)[el.dataset.op].branches[Number(el.dataset.b)];
    const c = b.conds[Number(el.dataset.i)];
    if (act === 'condfield') Object.assign(c, newCond(ent, ent.fields.find((x) => x.name === el.value)));
    else if (act === 'condop') { c.op = el.value; if (NO_VALUE.includes(c.op)) c.value = ''; }
    else c.value = el.value;
    render(); return;
  }
});

document.addEventListener('keydown', (ev) => {
  if ((ev.metaKey || ev.ctrlKey) && ev.key.toLowerCase() === 'k') {
    ev.preventDefault();
    state.ai = false;
    state.overlay = state.overlay?.kind === 'palette' ? null : { kind: 'palette' };
    render();
  }
  if (ev.key === 'Escape' && (state.overlay || state.ai)) { state.overlay = null; state.ai = false; render(); }
});

window.addEventListener('hashchange', () => {
  const next = location.hash || '#/overview';
  if (!next.startsWith('#/schema/')) state.tab = 'fields';
  state.route = next;
  state.overlay = null;
  state.ai = false;
  render();
});

/* --- Prototype chrome ---------------------------------------------------- */

const systemDark = window.matchMedia('(prefers-color-scheme: dark)').matches;
document.documentElement.setAttribute('data-theme', systemDark ? 'dark' : 'light');
$('#theme').textContent = systemDark ? 'Dark' : 'Light';

$('#theme').addEventListener('click', () => {
  const next = document.documentElement.getAttribute('data-theme') === 'dark' ? 'light' : 'dark';
  document.documentElement.setAttribute('data-theme', next);
  $('#theme').textContent = next === 'dark' ? 'Dark' : 'Light';
});

$('#density').addEventListener('click', () => {
  const next = document.documentElement.getAttribute('data-density') === 'comfortable' ? 'compact' : 'comfortable';
  document.documentElement.setAttribute('data-density', next);
  $('#density').textContent = next === 'comfortable' ? 'Comfortable' : 'Compact';
});

if (!location.hash) location.hash = '#/overview';
render();
