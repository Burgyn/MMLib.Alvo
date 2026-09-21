/* The prototype's content. Every value here is taken from the repository so
   the drawing cannot drift from what Alvo actually does:

   - the descriptor is examples/field-service/field-service.alvo.json, verbatim
   - the refusals are UnhonouredFeatures.cs, verbatim
   - the warnings are UnhonouredSubsystems.cs, verbatim
   - the operations are IAlvoManagement.cs
   - hooks, actions, computed and rollup are schema/project.schema.json
*/

// --- Refused: a control whose only possible output is a descriptor the apply
// --- rejects. Wording is UnhonouredFeatures.cs, not a paraphrase.
export const REFUSED = {
  'field.validation': {
    label: 'Custom validation rule',
    consequence:
      "Field 'validation' is not evaluated yet, so a value the expression forbids is accepted — the field is not constrained at all.",
    fix: "Remove 'validation'. Enforce the rule in a before-hook, or express it with a facet the API does validate — 'maxLength', 'precision'/'scale', enum 'values' or a 'format'.",
  },
  'field.default': {
    label: 'Value when none is given',
    consequence:
      "Field 'default' is not honoured yet: no column default is emitted and the value is dropped before any writer sees it, so the field is simply null — and on a 'required' field that is an INSERT of NULL into a NOT NULL column.",
    fix: "Remove 'default' and send the value explicitly on create. Refused rather than ignored because a silently absent default is a wrong stored value, which costs more than sending the field.",
  },
  'entity.softDelete': {
    label: 'Keep deleted records recoverable',
    consequence:
      'Soft delete is not supported yet: a delete would remove the row outright and reads would not exclude it, which is irrecoverable data loss where the schema promises recoverability.',
    fix: "Remove 'softDelete' or track the soft-delete implementation issue. A flag written as false is not a declaration and maps normally.",
  },
  'rollup.where': {
    label: 'Only count some child records',
    consequence:
      "A rollup's 'where' filter is not evaluated yet: the aggregate is still maintained, but it aggregates every record of the child entity instead of the subset this filter declares — a stored number that is silently wrong rather than absent.",
    fix: "Remove 'where' and aggregate every child record, or move the distinction into the model: a separate child entity, or a second rollup once filtered rollups land.",
  },
  JSONata: {
    label: 'Reshape the payload',
    consequence:
      "JSONata transformations are not evaluated yet: the action still runs, but with Alvo's canonical event envelope as its body instead of the transformation declared here — a delivery that succeeded carrying data you did not declare.",
    fix: 'Use a ‘{{…}}’ template instead (e.g. "{{new.title}}"), which this build does render, or remove the transformation and accept the canonical envelope.',
  },
};

// --- Warned: the descriptor applies, nothing runs. Wording is
// --- UnhonouredSubsystems.cs, served verbatim and never rewritten in the UI.
export const WARNED = {
  dynamicEntities:
    'no runtime entity can be created and the whole dynamic schema-registry driver is absent, so every governance limit declared here bounds nothing (F7)',
  automation:
    'no rule is ever evaluated, so no declared action runs — which looks exactly like a condition that never matched',
  templates:
    "a template referenced by an after-hook 'email' action is rendered, but one referenced only from an automation rule is not, because no rule is evaluated yet — and a 'bodyFile' is not read on either path",
  webhooks:
    "an endpoint an after-hook posts to is delivered to, but one referenced only from an automation rule never receives anything; and no delivery is signed — 'secretRef' is not read and no Standard Webhooks HMAC header is sent, so a receiver cannot yet verify the sender",
  functions: 'no function is ever invoked, on any trigger or schedule it declares',
};

/* Field types. One flat, ordered list: the eleven fit in a tidy grid, and a
   grid of equal chips reads faster than seven ragged rows of groups. The
   description belongs to whichever type is selected, not to all eleven at
   once — one sentence in one place beats eleven captions. */
export const TYPES = [
  ['string', 'One line of text, with a length limit you set.'],
  ['text', 'Long form text. No length limit, and not something to sort by.'],
  ['integer', 'A whole number. Counts, priorities, quantities.'],
  ['decimal', 'A number with a fixed number of decimal places. Money belongs here, never in a float.'],
  ['boolean', 'True or false, and nothing between.'],
  ['date', 'A day, with no time and no zone.'],
  ['datetime', 'A moment, stored in UTC and returned in UTC.'],
  ['enum', 'One of a fixed set you name. Alvo refuses any other value.'],
  ['ref', 'Points at a record of another entity. This field is the relationship.'],
  ['uuid', 'An identifier from somewhere else — a user id, an external key.'],
  ['json', 'Anything, stored as-is. Alvo will not validate or index inside it.'],
];

/* Which extra settings each type may carry. This is not a design choice: it is
   `$defs/field` in the frozen schema — nine if/then rules plus
   additionalProperties:false. `maxLength` on an integer, or `values` on a
   string, is refused at apply. `needs` are required BY the schema, so a field
   of that type has no valid descriptor without them. */
export const FACETS = {
  string:   { optional: ['maxLength', 'format'], needs: [] },
  text:     { optional: [], needs: [] },
  integer:  { optional: [], needs: [] },
  decimal:  { optional: [], needs: ['precision', 'scale'] },
  boolean:  { optional: [], needs: [] },
  date:     { optional: [], needs: [] },
  datetime: { optional: [], needs: [] },
  enum:     { optional: [], needs: ['values'] },
  ref:      { optional: ['onDelete'], needs: ['entity'] },
  uuid:     { optional: [], needs: [] },
  json:     { optional: [], needs: [] },
};

export const PROJECT = {
  name: 'field-service',
  description:
    'A multi-tenant field-service dispatch backend: shared region reference data, per-tenant customers, and per-tenant work orders assigned to technicians.',
  revision: 7,
  engine: 'PostgreSQL 16',
  mode: 'standalone',
  version: '0.5.0-preview.3',
  roles: ['dispatcher', 'technician'],
};

export const TENANTS = [
  { id: 'nordreg', name: 'Nordreg Facilities', records: 14203 },
  { id: 'bytehouse', name: 'Bytehouse s.r.o.', records: 10477 },
];

export const ENTITIES = [
  {
    name: 'regions',
    label: 'Regions',
    description:
      'A service region. Shared reference data (a číselník): global rather than tenant-scoped, so every tenant reads the same rows.',
    tenancy: 'global',
    audit: false,
    rows: 12,
    fields: [
      { name: 'code', type: 'string', required: true, unique: true, maxLength: 12, description: "The region's short code, unique across the instance." },
      { name: 'name', type: 'string', required: true, maxLength: 80, description: "The region's display name." },
    ],
    rules: { list: "'authenticated' in @user.roles", get: "'authenticated' in @user.roles", create: "'admin' in @user.roles", update: '', delete: '' },
    indexes: [],
    hooks: [],
  },
  {
    name: 'customers',
    label: 'Customers',
    description:
      'A customer of one tenant. Deliberately NOT audited, so its rows carry no version: no ETag is minted for one and an If-Match naming a version is refused rather than ignored.',
    tenancy: 'scoped',
    audit: false,
    rows: 1840,
    fields: [
      { name: 'name', type: 'string', required: true, maxLength: 120, description: "The customer's display name." },
      { name: 'tier', type: 'enum', required: true, values: ['standard', 'priority'], description: 'Which service tier the customer is on.' },
      { name: 'email', type: 'string', maxLength: 160, format: 'email', description: 'Where to send correspondence.' },
      { name: 'phone', type: 'string', maxLength: 40, format: 'phone', description: 'Where to call.' },
      { name: 'notes', type: 'text', description: 'Free-form notes about the customer.' },
      { name: 'open_jobs', type: 'integer', rollup: { from: 'work_orders', op: 'count' }, description: 'How many work orders point at this customer. Maintained by Alvo, in the same transaction as the write that changes it.' },
    ],
    rules: {
      list: "'dispatcher' in @user.roles || 'admin' in @user.roles",
      get: "'dispatcher' in @user.roles || 'admin' in @user.roles",
      create: "'dispatcher' in @user.roles || 'admin' in @user.roles",
      update: "'dispatcher' in @user.roles || 'admin' in @user.roles",
      delete: "'admin' in @user.roles",
    },
    indexes: [{ fields: ['tier', 'name'] }],
    hooks: [],
  },
  {
    name: 'work_orders',
    label: 'Work orders',
    description:
      'One job to be carried out for a customer, in a region, by a technician. Audited, so every row carries a version and every write of one can be made conditional.',
    tenancy: 'scoped',
    audit: true,
    rows: 24680,
    fields: [
      { name: 'reference', type: 'string', required: true, unique: true, maxLength: 24, format: 'work-order-ref', description: 'The human-facing work-order reference, unique within the instance.' },
      { name: 'title', type: 'string', required: true, maxLength: 120, description: 'One line describing the job.' },
      { name: 'description', type: 'text', description: 'The full job description as the customer gave it.' },
      { name: 'status', type: 'enum', required: true, values: ['scheduled', 'in_progress', 'completed', 'cancelled'], description: 'Where the job is in its life cycle.' },
      { name: 'priority', type: 'integer', required: true, description: 'Dispatch priority, 1 (highest) to 5 (lowest).' },
      { name: 'quoted_price', type: 'decimal', precision: 10, scale: 2, description: 'What the customer was quoted, excluding tax.' },
      { name: 'tax', type: 'decimal', precision: 10, scale: 2, computed: 'quoted_price * 0.23', description: 'Tax on the quote. Derived from another field of the same row, so Alvo keeps it and nobody writes it.' },
      { name: 'is_emergency', type: 'boolean', description: 'Whether the job was raised as an emergency call-out.' },
      { name: 'scheduled_for', type: 'datetime', description: 'When the technician is due on site.' },
      { name: 'completed_on', type: 'date', description: 'The day the job was signed off, if it has been.' },
      { name: 'contact_email', type: 'string', maxLength: 160, format: 'email', description: 'Who to tell when the job is done.' },
      { name: 'metadata', type: 'json', description: 'Whatever the dispatch system attached to the job.' },
      { name: 'assigned_to', type: 'uuid', description: 'The technician the job is assigned to. The rules compare it with @user.id, which is what makes a technician’s list a subset rather than a refusal.' },
      { name: 'external_ref', type: 'string', maxLength: 64, readOnly: true, description: "The identifier this job carries in the customer's own system. Maintained by the integration, never by an API caller." },
      { name: 'internal_notes', type: 'text', hidden: true, description: 'Dispatcher-only commentary. Confidential: neither the value nor the fact that this field exists may reach a caller.' },
      { name: 'access_code', type: 'string', required: true, hidden: true, maxLength: 32, description: 'The site access code the caller must supply on create and can never read back.' },
      { name: 'customer_id', type: 'ref', entity: 'customers', required: true, onDelete: 'restrict', description: 'The customer this job is for.' },
      { name: 'region_id', type: 'ref', entity: 'regions', required: true, onDelete: 'restrict', description: 'The region the job is carried out in.' },
    ],
    rules: {
      list: "'dispatcher' in @user.roles || 'admin' in @user.roles || assigned_to == @user.id",
      get: "'dispatcher' in @user.roles || 'admin' in @user.roles || assigned_to == @user.id",
      create: "'dispatcher' in @user.roles || 'admin' in @user.roles",
      update: "'dispatcher' in @user.roles || 'admin' in @user.roles || assigned_to == @user.id",
      delete: "'dispatcher' in @user.roles || 'admin' in @user.roles",
    },
    indexes: [{ fields: ['status', 'priority'] }, { fields: ['assigned_to'] }],
    hooks: [
      { point: 'beforeCreate', when: 'in the same transaction', condition: 'is_emergency == true && priority > 2', action: { kind: 'reject', text: 'An emergency call-out must be priority 1 or 2.' } },
      { point: 'beforeUpdate', when: 'in the same transaction', condition: "new.status == 'completed' && old.completed_on == null", action: { kind: 'mutate', text: 'completed_on ← today' } },
      { point: 'afterCreate', when: 'after commit, from the outbox', condition: '', action: { kind: 'email', text: 'job-scheduled → {{new.contact_email}}' } },
      { point: 'afterUpdate', when: 'after commit, from the outbox', condition: "new.status == 'completed'", action: { kind: 'webhook', text: 'billing-system' } },
    ],
  },
];

export const WORK_ORDERS = [
  { id: 'wo_7f31a', reference: 'WO-100418', title: 'Boiler will not fire on cold start', customer: 'Nordreg Facilities', customerId: 'cu_41a2', status: 'in_progress', priority: 1, quoted_price: 480.0, scheduled_for: '2026-09-22 08:30', owner: 'Jana Kováčová', emergency: true, region: 'BA-CENTRE' },
  { id: 'wo_2c09d', reference: 'WO-100419', title: 'Annual HVAC service — floors 3–5', customer: 'Bytehouse s.r.o.', customerId: 'cu_77b9', status: 'scheduled', priority: 3, quoted_price: 1240.0, scheduled_for: '2026-09-24 09:00', owner: 'Martin Novák', emergency: false, region: 'BA-CENTRE' },
  { id: 'wo_a5e62', reference: 'WO-100420', title: 'Leak under kitchen sink, unit 12', customer: 'Lumen Digital', customerId: 'cu_0c31', status: 'completed', priority: 2, quoted_price: 190.0, scheduled_for: '2026-09-18 14:00', owner: 'Peter Horváth', emergency: false, region: 'KE-NORTH' },
  { id: 'wo_91bb4', reference: 'WO-100421', title: 'Emergency lift release — two passengers', customer: 'Vertex Media', customerId: 'cu_9e08', status: 'completed', priority: 1, quoted_price: 0.0, scheduled_for: '2026-09-17 21:40', owner: 'Jana Kováčová', emergency: true, region: 'BA-WEST' },
  { id: 'wo_4d780', reference: 'WO-100422', title: 'Replace fire door closer, stairwell B', customer: 'Nordic Freight', customerId: 'cu_5d14', status: 'scheduled', priority: 4, quoted_price: 320.0, scheduled_for: '2026-09-29 11:15', owner: 'Zuzana Malá', emergency: false, region: 'ZA-EAST' },
  { id: 'wo_6e15c', reference: 'WO-100423', title: 'Quarterly generator load test', customer: 'Kavka Labs', customerId: 'cu_3f77', status: 'scheduled', priority: 5, quoted_price: 760.0, scheduled_for: '2026-10-02 07:00', owner: 'Martin Novák', emergency: false, region: 'KE-NORTH' },
  { id: 'wo_08fa2', reference: 'WO-100424', title: 'Cold room holding at −4 °C, should be −8', customer: 'Nordreg Facilities', customerId: 'cu_41a2', status: 'in_progress', priority: 1, quoted_price: 615.0, scheduled_for: '2026-09-21 16:20', owner: 'Peter Horváth', emergency: true, region: 'BA-WEST' },
  { id: 'wo_b3c47', reference: 'WO-100425', title: 'Rewire reception lighting circuit', customer: 'Bytehouse s.r.o.', customerId: 'cu_77b9', status: 'cancelled', priority: 3, quoted_price: 2100.0, scheduled_for: null, owner: 'Zuzana Malá', emergency: false, region: 'BA-CENTRE' },
  { id: 'wo_ff920', reference: 'WO-100426', title: 'Roof drain clearance before autumn', customer: 'Lumen Digital', customerId: 'cu_0c31', status: 'scheduled', priority: 4, quoted_price: 440.0, scheduled_for: '2026-10-06 08:00', owner: 'Jana Kováčová', emergency: false, region: 'ZA-EAST' },
];

export const CUSTOMERS = [
  { id: 'cu_41a2', name: 'Nordreg Facilities', tier: 'priority', email: 'facilities@nordreg.sk', open_jobs: 2 },
  { id: 'cu_77b9', name: 'Bytehouse s.r.o.', tier: 'standard', email: 'spravca@bytehouse.sk', open_jobs: 2 },
  { id: 'cu_0c31', name: 'Lumen Digital', tier: 'standard', email: 'office@lumen.digital', open_jobs: 2 },
  { id: 'cu_9e08', name: 'Vertex Media', tier: 'priority', email: 'ops@vertexmedia.sk', open_jobs: 1 },
  { id: 'cu_5d14', name: 'Nordic Freight', tier: 'standard', email: 'depot@nordicfreight.eu', open_jobs: 1 },
  { id: 'cu_3f77', name: 'Kavka Labs', tier: 'priority', email: 'lab@kavka.io', open_jobs: 1 },
];

export const REGIONS = [
  { id: 'rg_ba1', code: 'BA-CENTRE', name: 'Bratislava centre' },
  { id: 'rg_ba2', code: 'BA-WEST', name: 'Bratislava west' },
  { id: 'rg_ke1', code: 'KE-NORTH', name: 'Ko\u0161ice north' },
  { id: 'rg_za1', code: 'ZA-EAST', name: '\u017dilina east' },
];

/* Who the simulator signs in as. A rule compares assigned_to with the caller's
   own id, so the simulator needs a real person on both sides of that
   comparison \u2014 an abstract "the record is theirs" toggle cannot answer a rule
   that also tests a field. */
export const CALLERS = [
  { role: 'admin', name: 'Jana Kov\u00e1\u010dov\u00e1' },
  { role: 'dispatcher', name: 'Martin Nov\u00e1k' },
  { role: 'technician', name: 'Peter Horv\u00e1th' },
];

export const REVISIONS = [
  { revision: 7, at: '2026-09-21 09:14', author: 'jana@field-service.sk', reason: 'Add access_code to work_orders so the site code stops living in description', rolledBackFrom: null },
  { revision: 6, at: '2026-09-18 16:02', author: 'alvo apply (CI)', reason: 'Index work_orders on assigned_to — the technician list was a sequential scan', rolledBackFrom: null },
  { revision: 5, at: '2026-09-15 11:47', author: 'martin@field-service.sk', reason: 'Restore revision 3: emergency flag as enum broke every existing row', rolledBackFrom: 4 },
  { revision: 4, at: '2026-09-15 10:22', author: 'martin@field-service.sk', reason: 'Model emergency as an enum rather than a boolean', rolledBackFrom: null },
  { revision: 3, at: '2026-09-09 08:31', author: 'jana@field-service.sk', reason: 'Widen work_orders.title to 120 characters', rolledBackFrom: null },
  { revision: 2, at: '2026-09-02 13:55', author: 'alvo apply (CI)', reason: 'Add regions as global reference data', rolledBackFrom: null },
  { revision: 1, at: '2026-08-28 09:00', author: 'bootstrap', reason: 'First apply', rolledBackFrom: null },
];

/* `roles` is what the identity store has ASSIGNED. What counts is the
   intersection with what the descriptor DECLARES, plus the built-ins —
   IRoleCatalogProvider's own rule, and it fails closed. Peter carries one that
   does not survive that intersection, which is the quiet case the screen has to
   make loud: it is an error nowhere, it simply never matches. */
export const USERS = [
  { email: 'jana@field-service.sk', name: 'Jana Kováčová', roles: ['admin', 'dispatcher'], seen: '4 minutes ago', bootstrap: true, self: true, via: 'local' },
  { email: 'martin@field-service.sk', name: 'Martin Novák', roles: ['dispatcher'], seen: '2 hours ago', bootstrap: false, self: false, via: 'local' },
  { email: 'peter@field-service.sk', name: 'Peter Horváth', roles: ['technician', 'billing-manager'], seen: 'yesterday', bootstrap: false, self: false, via: 'local' },
  { email: 'zuzana@field-service.sk', name: 'Zuzana Malá', roles: [], seen: '3 days ago', bootstrap: false, self: false, via: 'local' },
];

/* Always present, never declared. `anon` is every caller with no identity at
   all — the role that makes something public. */
export const BUILTIN_ROLES = [
  ['anon', 'Not signed in — anyone who can reach the URL'],
  ['authenticated', 'Any signed-in caller, whatever else they hold'],
  ['admin', 'The built-in administrator role'],
];

/* The three levels the frozen schema declares, with the grants
   ManagementOperations.cs actually gives them. There is no `editor`: a level
   the descriptor does not name is refused at apply. */
export const ACCESS_LEVELS = [
  { level: 'admin', predicate: "'admin' in @user.roles", grants: 'Everything a developer may do, plus settings \u2014 API keys, who holds which role, and deleting the project.' },
  { level: 'developer', predicate: "'dispatcher' in @user.roles", grants: 'Edit what the backend is: apply a descriptor, roll one back. Not settings.' },
  { level: 'viewer', predicate: "'technician' in @user.roles", grants: 'Read the schema, the descriptor, the revisions and the capabilities, and simulate a policy.' },
];

export const API_KEYS = [
  { name: 'dispatch-integration', prefix: 'alvo_sk_7Fq…', scopes: ['work_orders:read', 'work_orders:write'], created: '2026-08-30', lastUsed: '11 minutes ago' },
  { name: 'reporting-readonly', prefix: 'alvo_sk_2Xd…', scopes: ['*:read'], created: '2026-09-04', lastUsed: '6 hours ago' },
];

export const ENDPOINTS = [
  { name: 'billing-system', url: 'https://billing.internal.field-service.sk/hooks/alvo', secretRef: 'BILLING_HOOK_SECRET', usedBy: ['work_orders afterUpdate'] },
  { name: 'ops-slack', url: 'https://hooks.slack.com/services/…', secretRef: 'SLACK_HOOK_SECRET', usedBy: [] },
];

export const TEMPLATES = [
  { name: 'job-scheduled', subject: 'Your job {{new.reference}} is booked for {{new.scheduled_for}}', usedBy: ['work_orders afterCreate'], bodyFile: false },
  { name: 'job-completed', subject: '{{new.reference}} is done', usedBy: [], bodyFile: true },
];

export const AI_TOOLS = [
  ['Read the schema', 'GET /management/schema', 'live'],
  ['Read the descriptor', 'GET /management/descriptor', 'live'],
  ['Read what this build honours', 'GET /management/capabilities', 'live'],
  ['Read the revision history', 'GET /management/revisions', 'live'],
  ['Test a rule against a caller', 'POST /management/policy/simulate', 'live'],
  ['Check a change before it runs', 'PUT /management/descriptor?dryRun=true', 'live'],
  ['Query records', 'POST /api/{entity}/query', 'live'],
  ['Apply a change', 'PUT /management/descriptor', 'never'],
  ['Write records', 'POST /api/{entity}', 'never'],
];

export const PALETTE_ITEMS = [
  { label: 'Ask Alvo about this project', hint: 'assistant', route: '#/overview', ai: true },
  { label: 'Edit work_orders', hint: 'schema', route: '#/schema/work_orders' },
  { label: 'Browse work_orders', hint: 'data', route: '#/data/work_orders' },
  { label: 'Preview pending changes', hint: 'dry run', route: '#/schema/preview' },
  { label: 'Who can update a work order?', hint: 'rules', route: '#/rules' },
  { label: 'Configuration history', hint: 'g h', route: '#/history' },
  { label: 'Webhook endpoints and templates', hint: 'integrations', route: '#/integrations' },
  { label: 'Export descriptor as JSON', hint: 'action', route: '#/schema/transfer' },
];
