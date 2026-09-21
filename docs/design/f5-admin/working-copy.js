/* The one working copy.
   =====================

   The drawing grew three unapplied-change queues — schema edits, rule edits and the role
   catalogue — each with its own pending bar, all three pointing at one preview that rendered only
   the schema ones. The product has one document: `ManagementApplyRequest` takes one
   `DescriptorJson`, one `ExpectedRevision`, and appends one revision.

   So there is one applied document, one working document, and one diff between them. Every editor
   in the prototype mutates `working` through the small API at the bottom of this file; nothing
   anywhere else holds an edit.

   Design: docs/superpowers/specs/2026-09-18-f5-admin-dashboard-design.md §4.5. */

import { APPLIED_DESCRIPTOR } from './generated/descriptor.js';
import { SCHEMA_FACETS } from './generated/schema-facets.js';

const clone = (value) => JSON.parse(JSON.stringify(value));

/* -------------------------------------------------------------------------- JSON pointer */

const encode = (token) => String(token).replace(/~/g, '~0').replace(/\//g, '~1');

export function at(doc, pointer) {
  if (!pointer) return doc;
  let node = doc;
  for (const token of pointer.slice(1).split('/')) {
    const key = token.replace(/~1/g, '/').replace(/~0/g, '~');
    if (node === undefined || node === null) return undefined;
    node = node[key];
  }
  return node;
}

function setAt(doc, pointer, value) {
  const tokens = pointer.slice(1).split('/').map((t) => t.replace(/~1/g, '/').replace(/~0/g, '~'));
  const last = tokens.pop();
  let node = doc;
  for (const token of tokens) {
    if (node[token] === undefined) node[token] = {};
    node = node[token];
  }
  if (value === undefined) delete node[last];
  else node[last] = value;
}

const isObject = (value) => value !== null && typeof value === 'object' && !Array.isArray(value);
const same = (a, b) => JSON.stringify(a) === JSON.stringify(b);

/** Every leaf-ish difference between two documents, as JSON pointers. */
function differences(before, after, pointer = '', out = []) {
  if (same(before, after)) return out;

  if (isObject(before) && isObject(after)) {
    for (const key of new Set([...Object.keys(before), ...Object.keys(after)])) {
      differences(before[key], after[key], `${pointer}/${encode(key)}`, out);
    }
    return out;
  }

  out.push({ pointer, before, after });
  return out;
}

/* -------------------------------------------------------------------------- classification */

/** Which kind of edit a pointer is. The four kinds §4.5 names, in the order the preview shows. */
export const KINDS = [
  { key: 'schema', title: 'Schema', note: 'Entities, fields and indexes. These produce a migration.' },
  { key: 'rules', title: 'Rules', note: 'Who may read and write records. No migration — this changes policy, not storage.' },
  { key: 'roles', title: 'Role catalogue', note: 'The names a rule or a level may mention. No migration.' },
  { key: 'access', title: 'Who may manage this project', note: 'The three management levels. Applying a descriptor that changes these needs admin.' },
];

function kindOf(pointer) {
  if (pointer.startsWith('/access')) return 'access';
  if (pointer.startsWith('/auth/roles')) return 'roles';
  if (/^\/entities\/[^/]+\/rules\b/.test(pointer)) return 'rules';
  return 'schema';
}

const human = (pointer) => pointer.slice(1).split('/').map((t) => t.replace(/~1/g, '/')).join(' › ');

function label(pointer, before, after) {
  const parts = pointer.slice(1).split('/');
  if (parts[0] === 'entities' && parts.length === 2) {
    return after === undefined ? `Remove the entity ${parts[1]}` : `Add the entity ${parts[1]}`;
  }
  if (parts[0] === 'entities' && parts[2] === 'fields' && parts.length === 4) {
    return after === undefined
      ? `Remove ${parts[1]}.${parts[3]}`
      : `Add ${parts[1]}.${parts[3]}`;
  }
  if (parts[0] === 'entities' && parts[2] === 'fields' && parts.length >= 5) {
    return `${parts[1]}.${parts[3]} — ${parts.slice(4).join(' ')}`;
  }
  if (parts[0] === 'entities' && parts[2] === 'rules') {
    return `${parts[1]} — ${parts[3]}`;
  }
  if (pointer === '/auth' || pointer.startsWith('/auth/roles')) return 'auth.roles — the role catalogue';
  if (pointer === '/access') return after === undefined ? 'Remove the access block' : 'Declare who may manage this project';
  if (pointer.startsWith('/access/')) return `access.${parts[1]}`;
  if (pointer === '/webhooks') return 'webhooks.endpoints';
  if (pointer === '/templates') return 'templates';
  if (pointer === '/tenancy') return 'tenancy';
  return human(pointer);
}

/* -------------------------------------------------------------------------- the migration plan

   What the schema half of a change would do to the database, and which steps discard data. The
   framework's own guardrail is `MigrationOptions.AllowDestructive`, never implied — not by a dry
   run that reported the plan, and not by a caller's management level. */

const NARROWABLE = ['maxLength', 'precision', 'scale'];

export function plan(before, after) {
  const steps = [];
  const beforeEntities = before.entities ?? {};
  const afterEntities = after.entities ?? {};

  for (const name of Object.keys(afterEntities)) {
    if (!beforeEntities[name]) {
      steps.push({ entity: name, text: `Create the table for ${name}`, destructive: false });
    }
  }
  for (const name of Object.keys(beforeEntities)) {
    if (!afterEntities[name]) {
      steps.push({
        entity: name,
        text: `Drop the table for ${name}`,
        destructive: true,
        loses: `every record of ${name}`,
      });
    }
  }

  for (const name of Object.keys(afterEntities)) {
    const oldEntity = beforeEntities[name];
    const newEntity = afterEntities[name];
    if (!oldEntity) continue;

    const oldFields = oldEntity.fields ?? {};
    const newFields = newEntity.fields ?? {};

    for (const field of Object.keys(newFields)) {
      if (!oldFields[field]) {
        const required = newFields[field].required === true;
        steps.push({
          entity: name,
          text: `Add the column ${name}.${field}`,
          destructive: false,
          note: required
            ? 'Required, so every existing row needs a value — the apply fails on a non-empty table until one exists.'
            : undefined,
        });
      }
    }
    for (const field of Object.keys(oldFields)) {
      if (!newFields[field]) {
        steps.push({
          entity: name,
          text: `Drop the column ${name}.${field}`,
          destructive: true,
          loses: `every value stored in ${name}.${field}`,
        });
      }
    }
    for (const field of Object.keys(newFields)) {
      const oldField = oldFields[field];
      const newField = newFields[field];
      if (!oldField) continue;

      if (oldField.type !== newField.type) {
        steps.push({
          entity: name,
          text: `Change ${name}.${field} from ${oldField.type} to ${newField.type}`,
          destructive: true,
          loses: `every value in ${name}.${field} that the new type cannot hold`,
        });
      }
      for (const facet of NARROWABLE) {
        const from = oldField[facet];
        const to = newField[facet];
        if (from === to || from === undefined || to === undefined) continue;
        if (to < from) {
          steps.push({
            entity: name,
            text: `Narrow ${name}.${field} ${facet} from ${from} to ${to}`,
            destructive: true,
            loses: `anything in ${name}.${field} longer or more precise than ${to}`,
          });
        } else {
          steps.push({ entity: name, text: `Widen ${name}.${field} ${facet} from ${from} to ${to}`, destructive: false });
        }
      }
      if (!same(oldField.values, newField.values) && newField.type === 'enum') {
        const removed = (oldField.values ?? []).filter((v) => !(newField.values ?? []).includes(v));
        steps.push(
          removed.length
            ? {
                entity: name,
                text: `Remove ${removed.join(', ')} from ${name}.${field}`,
                destructive: true,
                loses: `every row of ${name} whose ${field} is ${removed.join(' or ')}`,
              }
            : { entity: name, text: `Extend the values of ${name}.${field}`, destructive: false });
      }
      if (!oldField.unique && newField.unique) {
        steps.push({
          entity: name,
          text: `Add a unique constraint on ${name}.${field}`,
          destructive: false,
          note: 'The apply fails rather than deletes if two rows already share a value.',
        });
      }
    }

    const oldIndexes = JSON.stringify(oldEntity.indexes ?? []);
    const newIndexes = JSON.stringify(newEntity.indexes ?? []);
    if (oldIndexes !== newIndexes) {
      steps.push({ entity: name, text: `Rebuild the indexes on ${name}`, destructive: false });
    }
  }

  return {
    steps,
    isEmpty: steps.length === 0,
    hasDestructiveChanges: steps.some((step) => step.destructive),
  };
}

/* -------------------------------------------------------------------------- rendering the JSON

   The pane renders the WORKING document, not the applied revision, and marks the lines that
   differ. The previous prototype rendered a typed projection of a hand-written model, which is
   how it managed to show `precision: 10` under a bar reading "2 changes not applied" — and how a
   Razor editor built the same way would silently narrow every descriptor it touched (§6.3
   criterion 3). */

export function renderLines(doc, applied) {
  const lines = [];

  const emit = (text, pointer, indent) => lines.push({ text: '  '.repeat(indent) + text, pointer });

  const walk = (value, pointer, indent, keyPrefix, trailing) => {
    if (Array.isArray(value)) {
      if (value.length === 0) {
        emit(`${keyPrefix}[]${trailing}`, pointer, indent);
        return;
      }
      emit(`${keyPrefix}[`, pointer, indent);
      value.forEach((item, index) => {
        walk(item, `${pointer}/${index}`, indent + 1, '', index === value.length - 1 ? '' : ',');
      });
      emit(`]${trailing}`, pointer, indent);
      return;
    }
    if (isObject(value)) {
      const keys = Object.keys(value);
      if (keys.length === 0) {
        emit(`${keyPrefix}{}${trailing}`, pointer, indent);
        return;
      }
      emit(`${keyPrefix}{`, pointer, indent);
      keys.forEach((key, index) => {
        walk(
          value[key],
          `${pointer}/${encode(key)}`,
          indent + 1,
          `"${key}": `,
          index === keys.length - 1 ? '' : ',',
        );
      });
      emit(`}${trailing}`, pointer, indent);
      return;
    }
    emit(`${keyPrefix}${JSON.stringify(value)}${trailing}`, pointer, indent);
  };

  walk(doc, '', 0, '', '');

  if (applied) {
    for (const line of lines) {
      const here = at(doc, line.pointer);
      const there = at(applied, line.pointer);
      if (same(here, there)) continue;

      /* A container whose CHILDREN differ is not itself a changed line — marking it would put a
         gutter on the opening brace of every ancestor up to the root, which marks the whole file
         for a one-digit edit. A container is marked only when the whole subtree arrived or left. */
      if (there === undefined) { line.mark = 'added'; continue; }
      if (here === undefined) { line.mark = 'removed'; continue; }
      if (isObject(here) || Array.isArray(here)) continue;
      line.mark = 'changed';
    }
  }
  return lines;
}

/* -------------------------------------------------------------------------- the history

   Sample content, and labelled as such: the CURRENT descriptor is the repository's own example,
   and each earlier revision is that document with one change undone — so comparing r3 with r4 and
   restoring either really does move the working copy. */

function withUndone(doc, undo) {
  const copy = clone(doc);
  undo(copy);
  return copy;
}

function buildHistory(head) {
  const r7 = clone(head);

  const r6 = withUndone(r7, (d) => {
    delete d.entities.work_orders.fields.access_code;
  });
  const r5 = withUndone(r6, (d) => {
    d.entities.work_orders.indexes = d.entities.work_orders.indexes.filter(
      (index) => index.fields[0] !== 'assigned_to',
    );
  });
  // r4 modelled is_emergency as an enum; r5 restored r3 because it broke every existing row.
  const r4 = withUndone(r5, (d) => {
    d.entities.work_orders.fields.is_emergency = {
      type: 'enum',
      description: 'Whether the job was raised as an emergency call-out.',
      values: ['no', 'yes'],
    };
  });
  const r3 = clone(r5);
  const r2 = withUndone(r3, (d) => {
    d.entities.work_orders.fields.title.maxLength = 80;
  });
  const r1 = withUndone(r2, (d) => {
    delete d.entities.regions;
    for (const entity of Object.values(d.entities)) {
      if (entity.fields.region_id) delete entity.fields.region_id;
    }
  });

  return [
    { revision: 7, at: '2026-09-21 09:14', author: 'jana@field-service.sk', reason: 'Add access_code to work_orders so the site code stops living in description', rolledBackFrom: null, descriptor: r7 },
    { revision: 6, at: '2026-09-18 16:02', author: 'alvo apply (CI)', reason: 'Index work_orders on assigned_to — the technician list was a sequential scan', rolledBackFrom: null, descriptor: r6 },
    { revision: 5, at: '2026-09-15 11:47', author: 'martin@field-service.sk', reason: 'Restore revision 3: emergency flag as enum broke every existing row', rolledBackFrom: 3, descriptor: r5 },
    { revision: 4, at: '2026-09-15 10:22', author: 'martin@field-service.sk', reason: 'Model emergency as an enum rather than a boolean', rolledBackFrom: null, descriptor: r4 },
    { revision: 3, at: '2026-09-09 08:31', author: 'jana@field-service.sk', reason: 'Widen work_orders.title to 120 characters', rolledBackFrom: null, descriptor: r3 },
    { revision: 2, at: '2026-09-02 13:55', author: 'alvo apply (CI)', reason: 'Add regions as global reference data', rolledBackFrom: null, descriptor: r2 },
    { revision: 1, at: '2026-08-28 09:00', author: 'bootstrap', reason: 'First apply', rolledBackFrom: null, descriptor: r1 },
  ];
}

/* -------------------------------------------------------------------------- the store */

function make(head, revision) {
  return {
    revision,
    applied: clone(head),
    working: clone(head),
    history: buildHistory(head),
    /** Membership changes are NOT in the queue: they are the identity store and take effect at
        once (§4.5). They are recorded so the UI can report them in the past tense. */
    membershipLog: [],
  };
}

export const wc = make(APPLIED_DESCRIPTOR, 7);

/** Empty instance — used by the first-run scenario, where no descriptor has been applied. */
export function startEmpty(name = 'my-project') {
  const blank = { $schema: APPLIED_DESCRIPTOR.$schema, apiVersion: APPLIED_DESCRIPTOR.apiVersion, name, entities: {} };
  Object.assign(wc, { revision: 0, applied: clone(blank), working: clone(blank), history: [], membershipLog: [] });
}

/** Back to the field-service example at revision 7. */
export function reset() {
  Object.assign(wc, make(APPLIED_DESCRIPTOR, 7));
}

export function changes() {
  return differences(wc.applied, wc.working).map(({ pointer, before, after }) => ({
    pointer,
    before,
    after,
    kind: kindOf(pointer),
    label: label(pointer, before, after),
  }));
}

export const count = () => changes().length;

export function grouped() {
  const all = changes();
  return KINDS.map((kind) => ({ ...kind, rows: all.filter((change) => change.kind === kind.key) }))
    .filter((group) => group.rows.length > 0);
}

/** True when this working copy changes `access`, which re-resolves the whole apply to admin. */
export const touchesAccess = () => changes().some((change) => change.kind === 'access');

export const workingPlan = () => plan(wc.applied, wc.working);

export function discard() {
  wc.working = clone(wc.applied);
}

export function apply({ author, reason, allowDestructive = false } = {}) {
  const migration = workingPlan();
  if (migration.hasDestructiveChanges && !allowDestructive) {
    return { applied: false, refusal: 'destructive-change' };
  }
  wc.revision += 1;
  wc.applied = clone(wc.working);
  wc.history.unshift({
    revision: wc.revision,
    at: new Date().toISOString().slice(0, 16).replace('T', ' '),
    author: author || 'unknown',
    reason: reason || '',
    rolledBackFrom: null,
    descriptor: clone(wc.applied),
  });
  return { applied: true, revision: wc.revision, plan: migration };
}

export function restore(revision, { author, allowDestructive = false } = {}) {
  const entry = wc.history.find((item) => item.revision === revision);
  if (!entry) return { applied: false, refusal: 'not-found' };
  wc.working = clone(entry.descriptor);
  const migration = workingPlan();
  if (migration.hasDestructiveChanges && !allowDestructive) {
    return { applied: false, refusal: 'destructive-change', plan: migration };
  }
  wc.revision += 1;
  wc.applied = clone(wc.working);
  wc.history.unshift({
    revision: wc.revision,
    at: new Date().toISOString().slice(0, 16).replace('T', ' '),
    author: author || 'unknown',
    reason: `Rollback to revision ${revision}`,
    rolledBackFrom: revision,
    descriptor: clone(wc.applied),
  });
  return { applied: true, revision: wc.revision, plan: migration };
}

/* -------------------------------------------------------------------------- the editing API

   Everything that can change the descriptor goes through here, so there is exactly one place an
   edit can come from and the diff above cannot be out of date. */

const entityOf = (name) => (wc.working.entities[name] ??= { fields: {} });

export const editors = {
  setField(entity, field, patch) {
    const target = entityOf(entity);
    target.fields[field] = { ...(target.fields[field] ?? {}), ...patch };
    for (const [key, value] of Object.entries(patch)) {
      if (value === undefined || value === null || value === '' || value === false) {
        delete target.fields[field][key];
      }
    }
  },
  replaceField(entity, field, value) {
    entityOf(entity).fields[field] = value;
  },
  renameField(entity, from, to) {
    const fields = entityOf(entity).fields;
    const rebuilt = {};
    for (const [key, value] of Object.entries(fields)) rebuilt[key === from ? to : key] = value;
    entityOf(entity).fields = rebuilt;
  },
  removeField(entity, field) {
    delete entityOf(entity).fields[field];
  },
  addEntity(name, shape) {
    wc.working.entities[name] = shape;
  },
  removeEntity(name) {
    delete wc.working.entities[name];
  },
  setEntity(name, patch) {
    Object.assign(entityOf(name), patch);
  },
  setRule(entity, operation, cel) {
    const target = entityOf(entity);
    target.rules ??= {};
    if (cel) target.rules[operation] = cel;
    else delete target.rules[operation];
    if (Object.keys(target.rules).length === 0) delete target.rules;
  },
  setIndexes(entity, indexes) {
    const target = entityOf(entity);
    if (indexes.length) target.indexes = indexes;
    else delete target.indexes;
  },
  setRoles(roles) {
    wc.working.auth ??= {};
    wc.working.auth.roles = [...roles];
  },
  setAccess(level, cel) {
    wc.working.access ??= {};
    if (cel) wc.working.access[level] = cel;
    else delete wc.working.access[level];
    if (Object.keys(wc.working.access).length === 0) delete wc.working.access;
  },
  setHooks(entity, hooks) {
    const target = entityOf(entity);
    if (hooks && Object.keys(hooks).length) target.hooks = hooks;
    else delete target.hooks;
  },
  setAt(pointer, value) {
    setAt(wc.working, pointer, value);
  },
};

/* -------------------------------------------------------------------------- a view for screens

   The screens want a flat entity shape. It is DERIVED from the working document on every read, so
   a screen cannot hold a stale copy and an edit is visible everywhere at once.

   AND IT NEVER HANDS OUT A NAME THE SCHEMA CANNOT CARRY.

   Every screen builds HTML from these names, and there are upwards of forty places one reaches
   `innerHTML`. Escaping each of them is a list the next author adds a forty-first entry to; this
   is one place, and it is also the correct product behaviour rather than a security patch bolted
   on: `schema/project.schema.json`'s `propertyNames` patterns are what the apply enforces, so a
   descriptor carrying a name outside them is one the apply refuses — and a drawing that renders it
   is drawing a state that cannot exist. It is replaced with a visible marker, and the view says
   `invalidName` so a screen can show the problem rather than the string.

   The descriptor pane is unaffected and deliberately so: it renders the stored JSON through
   `esc()`, which is where an operator SHOULD see what the document actually says. */

const NAME_PATTERNS = {
  entity: new RegExp(SCHEMA_FACETS.namePatterns.entity),
  field: new RegExp(SCHEMA_FACETS.namePatterns.field),
  identifier: new RegExp(SCHEMA_FACETS.namePatterns.identifier),
};

export const INVALID_NAME = '\u27e8invalid name\u27e9';

/** A name a screen may render, or a marker. `kind` is entity, field or identifier. */
export function safeName(value, kind = 'identifier') {
  return NAME_PATTERNS[kind].test(value) ? value : INVALID_NAME;
}

export const isValidName = (value, kind = 'identifier') => NAME_PATTERNS[kind].test(value);

export function entities(doc = wc.working) {
  return Object.entries(doc.entities ?? {}).map(([rawName, entity]) => {
    const name = safeName(rawName, 'entity');
    return {
      name,
      invalidName: name === INVALID_NAME,
      label: name.replace(/_/g, ' ').replace(/^./, (c) => c.toUpperCase()),
      description: entity.description ?? '',
      tenancy: entity.tenancy ?? (doc.tenancy?.enabled ? 'scoped' : 'global'),
      audit: entity.audit === true,
      softDelete: entity.softDelete === true,
      realtime: entity.realtime !== false,
      storage: entity.storage ?? 'physical',
      indexes: (entity.indexes ?? []).map((index) => ({
        ...index,
        fields: (index.fields ?? []).map((f) => safeName(f, 'field')),
      })),
      hooks: entity.hooks ?? {},
      rules: entity.rules ?? {},
      fields: Object.entries(entity.fields ?? {}).map(([rawField, field]) => ({
        ...field,
        name: safeName(rawField, 'field'),
        invalidName: !isValidName(rawField, 'field'),
        ...(field.type === 'ref' ? { entity: safeName(field.entity, 'entity') } : {}),
        ...(field.rollup ? { rollup: { ...field.rollup, from: safeName(field.rollup.from, 'entity'), ...(field.rollup.field ? { field: safeName(field.rollup.field, 'field') } : {}) } } : {}),
      })),
    };
  });
}

export const entityView = (name, doc = wc.working) => entities(doc).find((e) => e.name === name);

export const declaredRoles = (doc = wc.working) => (doc.auth?.roles ?? []).map((r) => safeName(r));
export const accessBlock = (doc = wc.working) => doc.access ?? {};
export const declaredFormats = (doc = wc.working) => Object.keys(doc.formats ?? {}).map((f) => safeName(f));
export const tenancyEnabled = (doc = wc.working) => doc.tenancy?.enabled === true;

/** The warned blocks this descriptor actually declares — `UnhonouredSubsystems.DeclaredBy`. */
export function declaresBlock(block, doc = wc.working) {
  const value = doc[block];
  if (value === undefined || value === null) return false;
  if (Array.isArray(value)) return value.length > 0;
  if (block === 'dynamicEntities') return value.enabled === true;
  if (block === 'webhooks') return (value.endpoints ?? []).length > 0;
  if (isObject(value)) return Object.keys(value).length > 0;
  return Boolean(value);
}
