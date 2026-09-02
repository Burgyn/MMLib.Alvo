// The load scenarios, one exec function and one Trend per measured shape.
//
// READ THIS BEFORE ADDING A SCENARIO. Adding a measurement is two edits and never a new
// harness: one exec function plus its Trend here, and one row in baselines/gate.json expressing
// the new shape's cost as a RATIO against `list_indexed`. See test/load/README.md.
//
// THREE PROPERTIES THIS FILE EXISTS TO HOLD, each of which is easy to lose by accident:
//
// 1. `constant-arrival-rate`, never a looping VU pool. A closed model sends less when the
//    server slows, so the percentiles improve as the server gets sick — coordinated omission,
//    and the most common way a load test reports health for a broken service.
//
// 2. Scenarios run SEQUENTIALLY, staggered by `startTime`. Two in flight contend with each
//    other and neither one's latency is attributable to its own shape.
//
// 3. Every list-shaped scenario is offered the SAME rate. A ratio between two scenarios only
//    means anything when both saw the same offered load; differing rates would make the ratio a
//    statement about the generator.
//
// Pass/fail is NOT here. k6's thresholds cannot express a ratio between two trends, and the A/B
// comparison spans two runs, so the verdict lives in one place — scripts/assert-load-baseline —
// and this file only produces numbers.

import http from 'k6/http';
import exec from 'k6/execution';
import { Trend } from 'k6/metrics';

const BASE = __ENV.BASE_URL;
const OUT = __ENV.OUT_DIR || '.';
const SUMMARY = __ENV.SUMMARY_NAME || 'summary.json';

const KEY_DISPATCHER = __ENV.KEY_DISPATCHER;
const KEY_TECH = __ENV.KEY_TECH;
const TENANT = __ENV.TENANT_NORTH;
const ROWS = Number(__ENV.ROWS);
const DEEP_CURSOR = __ENV.DEEP_CURSOR || '';

// Seconds per measured scenario, and the offered rates. The gate tier trades resolution for a
// job that fits beside two image builds; calibration trades time for a publishable number.
const DURATION = Number(__ENV.DURATION || '15');
const LIST_RATE = Number(__ENV.LIST_RATE || '40');
const READ_RATE = Number(__ENV.READ_RATE || '80');
const WRITE_RATE = Number(__ENV.WRITE_RATE || '20');
const DOC_RATE = Number(__ENV.DOC_RATE || '5');

// Which scenarios this run measures. The baseline arm of an A/B runs only the four absolute
// shapes, because a ratio needs no second arm.
const REQUESTED = (__ENV.SCENARIOS || '').split(',').map((s) => s.trim()).filter(Boolean);

const WARMUP_SECONDS = 10;

// The catalogue is the ONE list of what exists: its names drive the Trends, the scenario map and
// the `--scenarios` filter alike. `exec` is a string, and a function declaration is hoisted, so
// this can sit above the functions it names without a forward-reference problem.
const CATALOGUE = [
    { name: 'read_by_id', exec: 'readById', rate: READ_RATE },
    { name: 'list_indexed', exec: 'listIndexed', rate: LIST_RATE },
    { name: 'count_exact', exec: 'countExact', rate: LIST_RATE },
    { name: 'page_shallow', exec: 'pageShallow', rate: LIST_RATE },
    { name: 'page_deep', exec: 'pageDeep', rate: LIST_RATE, needsCursor: true },
    { name: 'sort_nullable', exec: 'sortNullable', rate: LIST_RATE },
    { name: 'row_policy', exec: 'rowPolicy', rate: LIST_RATE },
    { name: 'select_projection', exec: 'selectProjection', rate: LIST_RATE },
    { name: 'unindexed_filter', exec: 'unindexedFilter', rate: LIST_RATE },
    { name: 'create', exec: 'createWorkOrder', rate: WRITE_RATE },
    { name: 'openapi', exec: 'openapiDocument', rate: DOC_RATE },
];

// Every Trend is declared HERE, at module scope. k6 refuses `new Metric()` outside the init
// context — "metrics must be declared in the init context" — so a lazily created trend throws on
// every iteration of every scenario and the run records nothing at all. That does fail loudly
// (the guard rejects a summary with no scenarios) but it fails after a full run, so it is eager.
const trends = {};
for (const scenario of CATALOGUE) {
    trends[scenario.name] = new Trend(`alvo_${scenario.name}`, true);
}

function trend(name) {
    const known = trends[name];
    if (!known) {
        throw new Error(`scenario '${name}' has no Trend: add it to CATALOGUE`);
    }
    return known;
}

// A bounded timeout, not k6's 60-second default: a request that stalls for a minute would be
// recorded as latency and drag a percentile, where what it actually is is a failure.
const TIMEOUT = '10s';

const dispatcher = { timeout: TIMEOUT, headers: { 'X-Alvo-Api-Key': KEY_DISPATCHER } };
const technician = { timeout: TIMEOUT, headers: { 'X-Alvo-Api-Key': KEY_TECH } };
const counted = {
    timeout: TIMEOUT,
    headers: { 'X-Alvo-Api-Key': KEY_DISPATCHER, Prefer: 'count=exact' },
};

const REFERENCE_LIST = 'status=eq.scheduled&order=priority.asc&limit=50';
const TWO_TERM_SORT = 'status=eq.scheduled&order=priority.asc,reference.asc&limit=50';

// A row id the seed is guaranteed to have written. Derived from the ordinal rather than fixed, so
// the read scenario spreads over the table instead of measuring one hot buffer page.
function seededId() {
    const n = 1 + (exec.scenario.iterationInTest % ROWS);
    return `33333333-0001-4000-8000-${n.toString(16).padStart(12, '0')}`;
}

function record(name, response) {
    trend(name).add(response.timings.duration);
}

export function readById() {
    record('read_by_id', http.get(`${BASE}/api/work_orders/${seededId()}`, dispatcher));
}

export function listIndexed() {
    record('list_indexed', http.get(`${BASE}/api/work_orders?${REFERENCE_LIST}`, dispatcher));
}

export function countExact() {
    record('count_exact', http.get(`${BASE}/api/work_orders?${REFERENCE_LIST}`, counted));
}

// The denominator for `page_deep`: the same two-term sort, at the shallowest possible cursor.
// Sharing the sort shape is what isolates depth from sort width.
export function pageShallow() {
    record('page_shallow', http.get(`${BASE}/api/work_orders?${TWO_TERM_SORT}`, dispatcher));
}

export function pageDeep() {
    const url = `${BASE}/api/work_orders?${TWO_TERM_SORT}&after=${encodeURIComponent(DEEP_CURSOR)}`;
    record('page_deep', http.get(url, dispatcher));
}

// `scheduled_for` is nullable, so the order renders a portable CASE rank that no index on the
// sort key can serve (#178). `priority` is required and renders no rank at all.
export function sortNullable() {
    const url = `${BASE}/api/work_orders?status=eq.scheduled&order=scheduled_for.asc&limit=50`;
    record('sort_nullable', http.get(url, dispatcher));
}

// The same URL as `list_indexed`, answered for a caller whose `list` rule carries
// `assigned_to == @user.id`. One variable changes: the row predicate.
export function rowPolicy() {
    record('row_policy', http.get(`${BASE}/api/work_orders?${REFERENCE_LIST}`, technician));
}

export function selectProjection() {
    const url = `${BASE}/api/work_orders?${REFERENCE_LIST}&select=id,reference`;
    record('select_projection', http.get(url, dispatcher));
}

export function unindexedFilter() {
    const url = `${BASE}/api/work_orders?is_emergency=is.true&order=priority.asc&limit=50`;
    record('unindexed_filter', http.get(url, dispatcher));
}

// References must not collide with the seed's (1..ROWS north, 50 000 001.. south) nor with each
// other, and must stay inside the descriptor's four-to-eight digit `work-order-ref` format.
export function createWorkOrder() {
    const ordinal = 90000000 + (exec.scenario.iterationInTest % 9000000);
    const body = JSON.stringify({
        tenant_id: TENANT,
        reference: `WO-${ordinal}`,
        title: 'Created under load',
        status: 'scheduled',
        priority: 3,
        access_code: 'AC-LOAD',
        customer_id: `22222222-0001-4000-8000-${(1).toString(16).padStart(12, '0')}`,
        region_id: `11111111-0000-4000-8000-${(1).toString(16).padStart(12, '0')}`,
    });
    const options = {
        timeout: TIMEOUT,
        headers: { 'X-Alvo-Api-Key': KEY_DISPATCHER, 'Content-Type': 'application/json' },
    };
    record('create', http.post(`${BASE}/api/work_orders`, body, options));
}

export function openapiDocument() {
    record('openapi', http.get(`${BASE}/openapi/v1.json`, dispatcher));
}

// The warm-up is unrecorded on purpose: JIT, the EF model cache, the connection pool and
// PostgreSQL's plan cache all have a first-request cost that belongs to none of the shapes below.
export function warmup() {
    http.get(`${BASE}/api/work_orders?${REFERENCE_LIST}`, dispatcher);
    http.get(`${BASE}/api/work_orders/${seededId()}`, dispatcher);
    http.get(`${BASE}/openapi/v1.json`, dispatcher);
}

function selected() {
    const wanted = CATALOGUE.filter((s) => REQUESTED.length === 0 || REQUESTED.includes(s.name));
    return wanted.filter((s) => !s.needsCursor || DEEP_CURSOR !== '');
}

function build() {
    const scenarios = {
        warmup: {
            executor: 'constant-arrival-rate',
            rate: 10,
            timeUnit: '1s',
            duration: `${WARMUP_SECONDS}s`,
            preAllocatedVUs: 10,
            maxVUs: 40,
            exec: 'warmup',
            startTime: '0s',
            gracefulStop: '0s',
        },
    };

    let start = WARMUP_SECONDS;
    for (const scenario of selected()) {
        scenarios[scenario.name] = {
            executor: 'constant-arrival-rate',
            rate: scenario.rate,
            timeUnit: '1s',
            duration: `${DURATION}s`,
            // Generously above rate x expected latency: an under-allocated pool cannot start
            // iterations on time, and k6 reports that as dropped_iterations, which the guard
            // treats as a void run rather than a slow one.
            preAllocatedVUs: Math.max(20, Math.ceil(scenario.rate / 2)),
            maxVUs: Math.max(100, scenario.rate * 4),
            exec: scenario.exec,
            startTime: `${start}s`,
            gracefulStop: '2s',
        };
        start += DURATION + 2;
    }

    return scenarios;
}

export const options = {
    scenarios: build(),
    summaryTrendStats: ['avg', 'min', 'med', 'p(95)', 'p(99)', 'max'],
    // No thresholds on purpose: the verdict has one authority, and it is not this file.
    thresholds: {},
};

export function handleSummary(data) {
    const metrics = {};
    for (const [name, metric] of Object.entries(data.metrics)) {
        if (name.startsWith('alvo_')) {
            metrics[name.slice('alvo_'.length)] = metric.values;
        }
    }

    const report = {
        rows: ROWS,
        duration: DURATION,
        rates: { list: LIST_RATE, read: READ_RATE, write: WRITE_RATE, doc: DOC_RATE },
        scenarios: metrics,
        // The three numbers that decide whether the run is valid at all.
        http_req_failed: data.metrics.http_req_failed ? data.metrics.http_req_failed.values : null,
        dropped_iterations: data.metrics.dropped_iterations
            ? data.metrics.dropped_iterations.values
            : { count: 0 },
        iterations: data.metrics.iterations ? data.metrics.iterations.values : null,
    };

    const out = {};
    out[`${OUT}/${SUMMARY}`] = JSON.stringify(report, null, 2);
    return out;
}
