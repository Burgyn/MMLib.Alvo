/* Sample content — and that is all it is.
   ========================================

   Everything the prototype says about *Alvo* is generated into `generated/` from the repository.
   What is left here is the made-up material a drawing needs to look like a working instance:
   records, the people in the identity store, and the palette's list of places to go.

   The previous version of this file opened with "every value here is taken from the repository…
   verbatim", which was false in nine places. That claim is gone, and the things it was made about
   have moved to `generated/`.

   Two rules this file follows:

   - A tenant is a **uuid** and nothing stores a name for one. There is no tenant registry anywhere
     in `src/`, so no screen may show a tenant's "name" — that would be a control over a concept the
     product does not have. Rows carry a `tenant` and screens show the discriminator.
   - Nothing here declares a construct the example descriptor does not. `tax` (computed) and
     `open_jobs` (a rollup) used to live here and are not in
     `examples/field-service/field-service.alvo.json`; they were content invented to fill a screen.
     They are gone. Scenario 2 adds a rollup through the editor instead, which is a better
     demonstration anyway. */

/** The two tenant discriminators in the sample data. Uuids, because that is what they are. */
export const TENANTS = [
  { id: '9f1c4a20-7d38-4a5e-9c11-2b6e0d4f8a4e', short: '9f1c…8a4e' },
  { id: '3b77e512-0c94-4f2a-8d63-71a5cf90b2d8', short: '3b77…b2d8' },
];

/** Who exists in the identity store. `AlvoUser` is Id, Email, RoleNames, IsDisabled — plus the
    tenant grant §2.7 adds. There is no display name and no "last seen": no port supplies either. */
export const USERS = [
  {
    id: 'a1d4c77e-51f2-4b9a-8e30-6c2f9b1d4a05',
    email: 'jana@field-service.sk',
    roleNames: ['admin', 'dispatcher'],
    isDisabled: false,
    tenant: TENANTS[0].id,
    bootstrap: true,
    self: true,
  },
  {
    id: 'b2e5d88f-62a3-4c0b-9f41-7d30ac2e5b16',
    email: 'martin@field-service.sk',
    roleNames: ['dispatcher'],
    isDisabled: false,
    tenant: TENANTS[0].id,
    bootstrap: false,
    self: false,
  },
  {
    id: 'c3f6e990-73b4-4d1c-a052-8e41bd3f6c27',
    email: 'peter@field-service.sk',
    roleNames: ['technician', 'billing-manager'],
    isDisabled: false,
    tenant: TENANTS[0].id,
    bootstrap: false,
    self: false,
  },
  {
    id: 'd407fa01-84c5-4e2d-b163-9f52ce407d38',
    email: 'zuzana@field-service.sk',
    roleNames: [],
    isDisabled: true,
    tenant: TENANTS[1].id,
    bootstrap: false,
    self: false,
  },
];

export const BOOTSTRAP = USERS[0];

/** Built-in roles — `RoleCatalog`'s three, which are never declared. */
export const BUILTIN_ROLES = [
  ['anon', 'Not signed in — anyone who can reach the URL'],
  ['authenticated', 'Any signed-in caller, whatever else they hold'],
  ['admin', 'The built-in administrator role'],
];

/** Sample records. `tenant` is the discriminator the row actually carries. */
export const ROWS = {
  regions: [
    { id: 'aa01', code: 'BA-CENTRE', name: 'Bratislava centre', tenant: null },
    { id: 'aa02', code: 'BA-WEST', name: 'Bratislava west', tenant: null },
    { id: 'aa03', code: 'KE-NORTH', name: 'Košice north', tenant: null },
    { id: 'aa04', code: 'ZA-EAST', name: 'Žilina east', tenant: null },
  ],
  customers: [
    { id: 'cu_41a2', name: 'Nordreg Facilities', tier: 'priority', email: 'facilities@nordreg.sk', phone: '+421 903 111 222', notes: '', tenant: TENANTS[0].id },
    { id: 'cu_77b9', name: 'Bytehouse s.r.o.', tier: 'standard', email: 'spravca@bytehouse.sk', phone: '+421 905 333 444', notes: '', tenant: TENANTS[0].id },
    { id: 'cu_0c31', name: 'Lumen Digital', tier: 'standard', email: 'office@lumen.digital', phone: '+421 911 555 666', notes: '', tenant: TENANTS[0].id },
    { id: 'cu_9e08', name: 'Vertex Media', tier: 'priority', email: 'ops@vertexmedia.sk', phone: '+421 918 777 888', notes: '', tenant: TENANTS[0].id },
    { id: 'cu_5d14', name: 'Nordic Freight', tier: 'standard', email: 'depot@nordicfreight.eu', phone: '+421 902 999 000', notes: '', tenant: TENANTS[0].id },
    { id: 'cu_3f77', name: 'Kavka Labs', tier: 'priority', email: 'lab@kavka.io', phone: '+421 949 121 314', notes: '', tenant: TENANTS[1].id },
  ],
  work_orders: [
    { id: 'wo_7f31a', reference: 'WO-100418', title: 'Boiler will not fire on cold start', description: 'Fails on cold start, runs once warm.', status: 'in_progress', priority: 1, quoted_price: 480.0, is_emergency: true, scheduled_for: '2026-09-22 08:30', completed_on: null, contact_email: 'facilities@nordreg.sk', metadata: {}, assigned_to: USERS[0].id, external_ref: 'ACC-8814', customer_id: 'cu_41a2', region_id: 'aa01', tenant: TENANTS[0].id, version: 4 },
    { id: 'wo_2c09d', reference: 'WO-100419', title: 'Annual HVAC service — floors 3–5', description: 'Scheduled maintenance, three floors.', status: 'scheduled', priority: 3, quoted_price: 1240.0, is_emergency: false, scheduled_for: '2026-09-24 09:00', completed_on: null, contact_email: 'spravca@bytehouse.sk', metadata: {}, assigned_to: USERS[1].id, external_ref: 'ACC-8815', customer_id: 'cu_77b9', region_id: 'aa01', tenant: TENANTS[0].id, version: 2 },
    { id: 'wo_a5e62', reference: 'WO-100420', title: 'Leak under kitchen sink, unit 12', description: 'Reported by the tenant on the 14th.', status: 'completed', priority: 2, quoted_price: 190.0, is_emergency: false, scheduled_for: '2026-09-18 14:00', completed_on: '2026-09-18', contact_email: 'office@lumen.digital', metadata: {}, assigned_to: USERS[2].id, external_ref: 'ACC-8816', customer_id: 'cu_0c31', region_id: 'aa03', tenant: TENANTS[0].id, version: 7 },
    { id: 'wo_91bb4', reference: 'WO-100421', title: 'Emergency lift release — two passengers', description: 'Call-out, out of hours.', status: 'completed', priority: 1, quoted_price: 0.0, is_emergency: true, scheduled_for: '2026-09-17 21:40', completed_on: '2026-09-17', contact_email: 'ops@vertexmedia.sk', metadata: {}, assigned_to: USERS[0].id, external_ref: null, customer_id: 'cu_9e08', region_id: 'aa02', tenant: TENANTS[0].id, version: 3 },
    { id: 'wo_4d780', reference: 'WO-100422', title: 'Replace fire door closer, stairwell B', description: '', status: 'scheduled', priority: 4, quoted_price: 320.0, is_emergency: false, scheduled_for: '2026-09-29 11:15', completed_on: null, contact_email: 'depot@nordicfreight.eu', metadata: {}, assigned_to: null, external_ref: null, customer_id: 'cu_5d14', region_id: 'aa04', tenant: TENANTS[0].id, version: 1 },
    { id: 'wo_08fa2', reference: 'WO-100424', title: 'Cold room holding at −4 °C, should be −8', description: 'Compressor short-cycling.', status: 'in_progress', priority: 1, quoted_price: 615.0, is_emergency: true, scheduled_for: '2026-09-21 16:20', completed_on: null, contact_email: 'facilities@nordreg.sk', metadata: {}, assigned_to: USERS[2].id, external_ref: 'ACC-8818', customer_id: 'cu_41a2', region_id: 'aa02', tenant: TENANTS[0].id, version: 5 },
    { id: 'wo_b3c47', reference: 'WO-100425', title: 'Rewire reception lighting circuit', description: 'Cancelled by the customer.', status: 'cancelled', priority: 3, quoted_price: 2100.0, is_emergency: false, scheduled_for: null, completed_on: null, contact_email: 'spravca@bytehouse.sk', metadata: {}, assigned_to: null, external_ref: null, customer_id: 'cu_77b9', region_id: 'aa01', tenant: TENANTS[0].id, version: 2 },
    { id: 'wo_ff920', reference: 'WO-100426', title: 'Roof drain clearance before autumn', description: '', status: 'scheduled', priority: 4, quoted_price: 440.0, is_emergency: false, scheduled_for: '2026-10-06 08:00', completed_on: null, contact_email: 'office@lumen.digital', metadata: {}, assigned_to: USERS[0].id, external_ref: null, customer_id: 'cu_0c31', region_id: 'aa04', tenant: TENANTS[0].id, version: 1 },
  ],
};

/** How many rows each entity holds, for the counts a screen shows. Sample figures. */
export const ROW_COUNTS = { regions: 12, customers: 1840, work_orders: 24680 };

/** What this instance reports — `ManagementInfo`: version, mode, dataProvider, startupMode. */
export const INFO = {
  version: '0.5.0-preview.3',
  mode: 'standalone',
  dataProvider: 'EfAlvoData',
  startupMode: 'migrate',
};

export const PALETTE_ITEMS = [
  { label: 'Go to Overview', hint: 'g o', route: '#/overview' },
  { label: 'Edit the schema', hint: 'g s', route: '#/schema' },
  { label: 'Browse records', hint: 'g d', route: '#/data' },
  { label: 'Who can do what', hint: 'g r', route: '#/rules' },
  { label: 'People and roles', hint: 'g a', route: '#/access' },
  { label: 'Preview unapplied changes', hint: 'dry run', route: '#/schema/preview' },
  { label: 'Configuration history', hint: 'g h', route: '#/history' },
  { label: 'Webhook endpoints and templates', hint: 'g i', route: '#/integrations' },
  { label: 'Export the descriptor', hint: 'action', route: '#/schema/transfer' },
  { label: 'Design notes', hint: 'g n', route: '#/notes' },
];

/** `g` + letter jumps, the design's §5.5 list. */
export const GOTO = {
  o: '#/overview', s: '#/schema', d: '#/data', r: '#/rules',
  a: '#/access', h: '#/history', i: '#/integrations', n: '#/notes', t: '#/settings',
};
