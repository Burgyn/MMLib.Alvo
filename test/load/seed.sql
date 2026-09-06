-- The load-test seed: bulk rows for the field-service descriptor, written straight into the
-- physical tables.
--
-- WHY SQL AND NOT THE API. The calibration tier needs 200 000 work orders. Over HTTP that is
-- 200 000 POSTs; `AlvoDataSeed` is internal to the EF package, in-process, and routes every row
-- through the change tracker, so it is unreachable from a black-box harness and wrong for bulk
-- anyway. A set-based INSERT does it in seconds.
--
-- WHAT IT COSTS, AND HOW THAT COST IS KEPT HONEST. This file knows the physical layout that
-- `DescriptorToSchemaMapper` owns: table names, column names, and the framework-managed columns
-- (`id`, `tenant_id`, `created_at`, `created_by`, `updated_at`, `updated_by`). A rename would
-- make it wrong. It is not left to rot silently: `scripts/test-load` reads the same set back
-- THROUGH THE PUBLIC API with `Prefer: count=exact` and aborts unless the count matches what was
-- inserted here. An empty result set is the one failure mode that would otherwise report a
-- spectacular p95 for a list of nothing.
--
-- Identifiers are derived from the row ordinal rather than `gen_random_uuid()`, so two runs of
-- the same tier produce byte-identical data and a difference between two measurements cannot be
-- the data.
--
-- Parameters (psql -v):
--   rows            work orders PER TENANT
--   customers       customers PER TENANT
--   tenant_north    the north tenant's id  (examples/field-service/demo-identities.env)
--   tenant_south    the south tenant's id
--   tech_north      the tenant-north technician's user id, for the `assigned_to` row predicate

\set ON_ERROR_STOP on

BEGIN;

TRUNCATE work_orders, customers, regions CASCADE;

-- Eight shared regions. `regions` is `tenancy: global`, so it carries no tenant_id and is not
-- audited: every tenant reads the same rows.
INSERT INTO regions (id, code, name)
SELECT
    ('11111111-0000-4000-8000-' || lpad(to_hex(n), 12, '0'))::uuid,
    'R' || lpad(n::text, 3, '0'),
    'Region ' || n
FROM generate_series(1, 8) AS n;

-- Customers, per tenant. Unaudited by descriptor, so no audit quartet: the only framework column
-- is tenant_id.
INSERT INTO customers (id, tenant_id, name, tier, email, phone, notes)
SELECT
    ('22222222-' || lpad(to_hex(t.ordinal), 4, '0') || '-4000-8000-' || lpad(to_hex(n), 12, '0'))::uuid,
    t.id,
    'Customer ' || lpad(n::text, 6, '0'),
    CASE WHEN n % 5 = 0 THEN 'priority' ELSE 'standard' END,
    'customer' || n || '@example.test',
    '+421900' || lpad(n::text, 6, '0'),
    NULL
FROM generate_series(1, :customers) AS n
CROSS JOIN (VALUES (1, :'tenant_north'::uuid), (2, :'tenant_south'::uuid)) AS t(ordinal, id);

-- Work orders, per tenant. Audited, so the audit quartet is written here exactly as
-- `AlvoAuditStamp.Applied` would on a create: all four columns, `updated_at` equal to
-- `created_at`, because "last written" really is the creation instant for a row only created.
--
-- The distributions are chosen so each scenario has something to measure:
--   status         cycles over the four enum values -> `status=eq.scheduled` matches ~25%
--   priority       1..5, the second term of IX_work_orders_status_priority
--   scheduled_for  NULL for ~30% of rows, so ordering by it exercises the CASE rank (#178)
--   assigned_to    the tenant-north technician on ~10% of north's rows, so that caller's
--                  `assigned_to == @user.id` list is a genuine indexed subset, not empty
--   is_emergency   true for ~5%, an unindexed filter with a small result set
INSERT INTO work_orders (
    id, tenant_id, reference, title, description, status, priority, quoted_price,
    is_emergency, scheduled_for, completed_on, contact_email, metadata, assigned_to,
    external_ref, internal_notes, access_code, customer_id, region_id,
    created_at, created_by, updated_at, updated_by
)
SELECT
    ('33333333-' || lpad(to_hex(t.ordinal), 4, '0') || '-4000-8000-' || lpad(to_hex(n), 12, '0'))::uuid,
    t.id,
    -- `lpad` TRUNCATES a value longer than its width, so the per-tenant offset has to keep the
    -- ordinal inside eight digits or two tenants collapse onto one reference. 50 000 000 leaves
    -- room for the 1 M-row variant on both sides. The format itself is the descriptor's
    -- `work-order-ref`: WO- followed by four to eight digits.
    'WO-' || lpad(((t.ordinal - 1) * 50000000 + n)::text, 8, '0'),
    'Work order ' || lpad(n::text, 8, '0'),
    'Seeded by test/load/seed.sql for load measurement.',
    (ARRAY['scheduled', 'in_progress', 'completed', 'cancelled'])[1 + (n % 4)],
    1 + (n % 5),
    round((n % 5000)::numeric / 100, 2),
    (n % 20 = 0),
    CASE WHEN n % 10 < 3 THEN NULL
         ELSE timestamptz '2026-01-01 08:00:00+00' + (n % 500) * interval '1 hour' END,
    CASE WHEN n % 4 = 2 THEN date '2026-01-01' + (n % 300) ELSE NULL END,
    'contact' || n || '@example.test',
    NULL,
    CASE WHEN t.ordinal = 1 AND n % 10 = 0 THEN :'tech_north'::uuid ELSE NULL END,
    NULL,
    NULL,
    'AC' || lpad(n::text, 8, '0'),
    ('22222222-' || lpad(to_hex(t.ordinal), 4, '0') || '-4000-8000-'
        || lpad(to_hex(1 + (n % :customers)), 12, '0'))::uuid,
    ('11111111-0000-4000-8000-' || lpad(to_hex(1 + (n % 8)), 12, '0'))::uuid,
    timestamptz '2026-01-01 00:00:00+00' + n * interval '1 second',
    NULL,
    timestamptz '2026-01-01 00:00:00+00' + n * interval '1 second',
    NULL
FROM generate_series(1, :rows) AS n
CROSS JOIN (VALUES (1, :'tenant_north'::uuid), (2, :'tenant_south'::uuid)) AS t(ordinal, id);

COMMIT;

-- ANALYZE, not VACUUM FULL: the planner needs statistics over a set that went from empty to
-- hundreds of thousands of rows in one transaction, and without them the first scenario measures
-- a bad plan rather than the query.
ANALYZE regions;
ANALYZE customers;
ANALYZE work_orders;
