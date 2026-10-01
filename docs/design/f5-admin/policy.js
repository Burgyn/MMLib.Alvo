/* What the simulator renders.
   ===========================

   `POST {m}/projects/{p}/policy/simulate` takes an entity, an operation and a caller. It takes
   **no record id**, and `ManagementPolicySimulation`'s own remark says why: evaluating a predicate
   against a stored row needs a read, and a read through the Management API is the data surface
   deviation D4 refuses to create.

   The previous prototype picked a real record and answered allowed / refused per operation. That
   is a second policy evaluator, and it fails F5 acceptance criterion §6.3-4 by construction —
   whatever it answers. The moment it disagrees with `IPolicyEngine` (over a null comparison, over
   role-name ordinality, over the tenant guard's precedence) the dashboard teaches the wrong thing
   with total confidence.

   So this module produces `ManagementPolicyVerdict`'s shape and nothing else, and it reproduces
   `PolicyEngine.Resolve`'s order rather than inventing one. Nothing here reads a record.

   Design: §2.2.1. */

import { entityView, tenancyEnabled } from './working-copy.js';

export const OPERATIONS = ['list', 'get', 'create', 'update', 'delete'];

const TENANT_SCOPE_SOURCE = 'tenant_id == @tenant.id';

/** `PolicyEngine.DenyReasonForOperation` — one wording, the framework's own. */
const noPolicy = (operation) => `No policy allows '${operation}' on this entity.`;

/** Which slots an operation needs, exactly `PolicyCatalogBuilder.CompileRules`' mapping. */
function slots(operation, rules) {
  const source = rules?.[operation] ?? null;
  switch (operation) {
    case 'create':
      return { using: null, withCheck: source };
    case 'update':
      // update compiles its source once and reuses the same expression for both slots.
      return { using: source, withCheck: source };
    default:
      return { using: source, withCheck: null };
  }
}

/** `PolicyEngine.IsUnconfigured`. */
function unconfigured(operation, { using: usingSource, withCheck }) {
  if (operation === 'create') return withCheck === null;
  if (operation === 'update') return usingSource === null || withCheck === null;
  return usingSource === null;
}

const readsUserId = (cel) => typeof cel === 'string' && cel.includes('@user.id');
const readsTenantId = (cel) => typeof cel === 'string' && cel.includes('@tenant.id');

/** Which fields this caller may not read / may not write, from a `hidden` / `readOnly` facet. */
function mask(fields, facet, caller) {
  return fields
    .filter((field) => {
      const value = field[facet];
      if (value === true) return true;
      if (typeof value !== 'string') return false;
      // A CEL-valued mask: the honest answer is that it depends on the caller, and the one thing
      // this client can say without evaluating a row is which roles the expression names.
      return !rolesNamedIn(value).some((role) => caller.roles.includes(role));
    })
    .map((field) => field.name);
}

export function rolesNamedIn(cel) {
  return [...String(cel ?? '').matchAll(/'([^']+)'\s+in\s+@user\.roles/g)].map((m) => m[1]);
}

/**
 * The verdict for one caller and one operation.
 *
 * @param {string} entityName
 * @param {string} operation one of OPERATIONS
 * @param {{user: string|null, roles: string[], tenant: string|null}} caller
 * @param {object} doc the descriptor to answer against (working or applied)
 */
export function verdict(entityName, operation, caller, doc) {
  const entity = entityView(entityName, doc);

  // 1. No descriptor applied yet.
  if (!doc || Object.keys(doc.entities ?? {}).length === 0) {
    return deny('No descriptor has been applied yet; no policy is configured.', 'no-descriptor');
  }

  // 2. Unknown entity — deliberately indistinguishable from "not authorised".
  if (!entity) {
    return deny(noPolicy(operation), 'unknown-entity');
  }

  const scoped = entity.tenancy === 'scoped' && tenancyEnabled(doc);
  const tenantScope = scoped ? TENANT_SCOPE_SOURCE : null;

  // 3. The tenant guard, before any rule is consulted.
  if (scoped && !caller.tenant) {
    return deny('The caller has no tenant, and this entity is tenant-scoped.', 'tenant-guard', { tenantScope });
  }

  const resolved = slots(operation, entity.rules);

  // 4. An unconfigured operation.
  if (unconfigured(operation, resolved)) {
    return deny(noPolicy(operation), 'unconfigured', { tenantScope });
  }

  // 5. A missing required context value, tenant before user — the engine's own order.
  const reads = [resolved.using, resolved.withCheck, tenantScope];
  if (reads.some(readsTenantId) && !caller.tenant) {
    return deny('The caller has no tenant, and the policy for this operation reads one.', 'required-context', { tenantScope });
  }
  if (reads.some(readsUserId) && !caller.user) {
    return deny('The caller has no identity, and the policy for this operation reads one.', 'required-context', { tenantScope });
  }

  return {
    allowed: true,
    cause: null,
    denyReason: null,
    using: resolved.using,
    withCheck: resolved.withCheck,
    tenantScope,
    hiddenFields: mask(entity.fields, 'hidden', caller),
    readOnlyFields: mask(entity.fields, 'readOnly', caller),
  };
}

function deny(reason, cause, extra = {}) {
  return {
    allowed: false,
    cause,
    denyReason: reason,
    using: null,
    withCheck: null,
    tenantScope: null,
    hiddenFields: [],
    readOnlyFields: [],
    ...extra,
  };
}

/* -------------------------------------------------------------------------- what it means

   `allowed` is NOT "this caller will see rows". A rule over @user.roles is a predicate the engine
   hands back rather than evaluates, so a caller no rule admits still earns `true` here together
   with a `Using` none of their rows satisfies. Every sentence below exists so no screen can render
   `allowed` alone. */

/** The four ways a caller actually gets 403, named for a human, in the engine's own order. */
export const CAUSES = {
  'no-descriptor': {
    title: 'Nothing is applied yet',
    detail: 'No descriptor has been applied, so no policy exists to consult. 403 on every operation of every entity.',
  },
  'unknown-entity': {
    title: 'No rule is configured, or there is no such entity',
    detail:
      'The engine makes “no such entity” and “not authorised” deliberately indistinguishable, so this one answer covers both. Default-deny: an operation nobody wrote a rule for is refused for everyone.',
  },
  'tenant-guard': {
    title: 'The caller carries no tenant',
    detail:
      'This entity is tenancy: scoped, and the guard refuses before any rule is consulted. Not an empty page — a 403. A signed-in operator acquires a tenant in Access; a key carries the one it was issued for.',
  },
  unconfigured: {
    title: 'That operation has no rule',
    detail:
      'list, get and delete need a read predicate; create needs a write predicate; update needs both. A missing one is refused for everyone, including an administrator.',
  },
  'required-context': {
    title: 'The predicate reads something this caller has not got',
    detail:
      'The rule mentions @user.id or @tenant.id and the caller carries neither. Refused before the predicate is handed out, because a predicate over a value nobody has can only be a trap.',
  },
};

/** What failing the read predicate actually looks like, per operation. The RLS surprise. */
export function outcomeOfFailingUsing(operation) {
  if (operation === 'list') {
    return {
      status: '200',
      title: 'a shorter list, not an error',
      detail:
        'A configured rule compiles to a row-level USING predicate. A caller who fails it gets 200 with an empty page — Postgres RLS semantics, and the single most confusing thing about the API.',
    };
  }
  if (operation === 'create') {
    return {
      status: '422',
      title: 'the write is refused',
      detail: 'A create is judged by WITH CHECK over the row being written, so failing it refuses the write rather than hiding anything.',
    };
  }
  return {
    status: '404',
    title: 'not found — deliberately indistinguishable from “there is no such row”',
    detail:
      'One row, one answer: a row-level exclusion on get, update or delete is a 404, because “invisible to me” and “not there” must not be tellable apart.',
  };
}

/** The sentence that must accompany `allowed`, so nothing renders it as “permitted”. */
export const ALLOWED_MEANS =
  'The engine resolved a policy — it has not said this caller will see rows. A role predicate is handed back, not evaluated, so a caller no rule admits still lands here with a predicate none of their rows satisfies.';
