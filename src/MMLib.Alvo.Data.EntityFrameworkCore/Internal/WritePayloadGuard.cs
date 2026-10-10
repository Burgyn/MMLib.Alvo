using MMLib.Alvo.Rules;
using MMLib.Alvo.Schema;

namespace MMLib.Alvo.Data.EntityFrameworkCore;

/// <summary>
/// Refuses a write payload before any row is looked up: a key the entity does not declare, a field the
/// policy marks read-only, the framework-managed columns a caller may never set, and a field whose value is
/// maintained for it — a <c>computed</c> field by the engine, a <c>rollup</c> by the framework.
/// </summary>
/// <remarks>
/// <para>
/// Every refusal here is <see cref="AlvoAuthorizationException"/> and every one of them is decided from
/// the payload alone, so a caller cannot use "was my write rejected" to learn whether a row id exists —
/// the row was never consulted.
/// </para>
/// <para>
/// <b>Which columns are framework-managed is asked, not remembered.</b>
/// <see cref="AlvoManagedColumns.For(EntitySchema)"/> answers it from the entity's own traits — the same
/// question, of the same inputs, that the descriptor mapper injects those columns from. This method used
/// to name two columns while the mapper injected six, and the four it did not know about were
/// caller-writable: a create could assert a victim authored the row and an update could back-date it,
/// with no rule violated on either engine. An enumeration in the guard is exactly what went stale, so
/// there is none.
/// </para>
/// <para>
/// The one asymmetry is <c>tenant_id</c> on a create, and it lives in
/// <see cref="AlvoManagedColumns.IsCallerWritable"/> with its reason: a create legitimately places a row
/// in a tenant, and the synthesized tenant scope over the candidate row decides whether that tenant is
/// allowed. No managed column is ever a descriptor-declared field on an entity the framework manages it
/// for, so none can appear in <see cref="PolicyDecision.ReadOnlyFields"/> — the read-only check alone
/// would let every one of them through.
/// </para>
/// <para>
/// A <c>hidden</c> field is deliberately still writable: <c>hidden</c> restricts reading, and refusing a
/// write to one would tell the caller the field exists.
/// </para>
/// <para>
/// The messages are word for word <c>InMemoryAlvoData</c>'s, because both read them from
/// <see cref="AlvoManagedColumns.RefusalReason"/> — the reference implementation and this one answer the
/// same refusal with the same text, and the adversarial suite asserts on the read-only message naming its
/// field.
/// </para>
/// </remarks>
internal static class WritePayloadGuard
{
    /// <summary>Refuses <paramref name="values"/> if any key is unwritable on this path.</summary>
    /// <param name="values">The caller-supplied payload.</param>
    /// <param name="entity">The entity being written, or <see langword="null"/> when the applied schema does not declare it.</param>
    /// <param name="decision">The verdict <see cref="IPolicyEngine"/> returned for this caller.</param>
    /// <param name="isUpdate">Whether this is an update rather than a create.</param>
    /// <exception cref="AlvoAuthorizationException">A key is undeclared, framework-managed or read-only.</exception>
    internal static void EnsureWritable(
        IReadOnlyDictionary<string, object?> values, EntitySchema? entity, PolicyDecision decision, bool isUpdate)
    {
        if (PayloadRefusal(values, entity, decision, isUpdate) is { } reason)
        {
            throw new AlvoAuthorizationException(reason);
        }
    }

    /// <summary>
    /// Why <paramref name="values"/> may not be written, or <see langword="null"/> when every key is writable.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The one evaluation of these five rules; <see cref="EnsureWritable"/> is a caller of it.</b> A batch
    /// reports every bad row rather than the first, so it needs the verdict without a throw — and a second,
    /// collecting copy of a rule is how two copies of one rule come to differ.
    /// </para>
    /// <para>
    /// <b><see cref="QueryFieldGuard.EnsureDeclared"/> is caught rather than inverted</b>, because it is the
    /// <em>read</em> path's guard as well: inverting it would change a second surface for a batch's
    /// convenience, and an undeclared name has to read identically on both. The two
    /// <see cref="ArgumentNullException"/> guards stay throwing, because they are the port's fifth failure
    /// family — a broken caller, not a refused one — and <c>WritePayloadGuardTests</c> pins that.
    /// </para>
    /// </remarks>
    /// <param name="values">The caller-supplied payload.</param>
    /// <param name="entity">The entity being written, or <see langword="null"/> when the applied schema does not declare it.</param>
    /// <param name="decision">The verdict <see cref="IPolicyEngine"/> returned for this caller.</param>
    /// <param name="isUpdate">Whether this is an update rather than a create.</param>
    internal static string? PayloadRefusal(
        IReadOnlyDictionary<string, object?> values, EntitySchema? entity, PolicyDecision decision, bool isUpdate)
    {
        ArgumentNullException.ThrowIfNull(values);
        ArgumentNullException.ThrowIfNull(decision);

        try
        {
            QueryFieldGuard.EnsureDeclared(values, entity);
        }
        catch (AlvoAuthorizationException undeclared)
        {
            return undeclared.Message;
        }

        return ManagedColumnRefusal(values, entity, isUpdate)
            ?? ComputedRefusal(values, entity)
            ?? RollupRefusal(values, entity)
            ?? ReadOnlyRefusal(values, decision.ReadOnlyFields);
    }

    /// <summary>
    /// Refuses a payload that names a <c>computed</c> field. The value is maintained by the <b>engine</b>, as a
    /// stored generated column, so there is no write for this port to perform.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Refused rather than dropped, and that is the whole point of stating it here.</b> The runtime model
    /// marks a computed property store-generated (<see cref="AlvoDataContext"/>), which makes EF leave the
    /// column out of the <c>INSERT</c> — so without this check a caller who sent <c>line_total: 999</c> would
    /// get a <c>201</c> whose body reports the engine's own value, with nothing anywhere saying their number was
    /// discarded. A payload that is silently ignored is the wrong-stored-number failure class this feature is
    /// otherwise built to remove, arriving from the caller's side instead of the schema's.
    /// </para>
    /// <para>
    /// The engine's refusal is still the guarantee — a write that reaches the column at all, from another
    /// application or a raw statement, is rejected by the database itself — and this is what turns that
    /// guarantee into an actionable answer for a caller who came through the port. The two are not redundant:
    /// this one names the field and the mechanism, the engine's one names neither.
    /// </para>
    /// <para>
    /// Its position in the order is immaterial to disclosure: like every refusal in this type it is decided from
    /// the payload and the schema alone, so no row is consulted and no answer here depends on stored data.
    /// </para>
    /// </remarks>
    private static string? ComputedRefusal(IReadOnlyDictionary<string, object?> values, EntitySchema? entity)
    {
        var computed = entity?.Fields
            .Where(field => field.ComputedExpression is not null)
            .FirstOrDefault(field => values.ContainsKey(field.Name));

        return computed is null
            ? null
            : $"Field '{computed.Name}' is computed by the database and cannot be written: it is a stored "
            + "generated column, so the engine itself refuses every write to it. Remove it from the "
            + "payload — its value follows from the fields the expression reads.";
    }

    /// <summary>
    /// Refuses a payload that names a <c>rollup</c> field. The value is maintained by <b>the framework</b> from the
    /// child rows, so a caller has nothing to write there (#342).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This arm is the whole guarantee, which is what separates it from <see cref="ComputedRefusal"/>.</b> A
    /// computed field is a stored generated column, so the engine refuses a write the port let through. A rollup is
    /// an <em>ordinary</em> column: <c>RollupRecompute</c> keeps it with a raw <c>UPDATE</c> inside a child write's
    /// transaction, and no constraint on either engine stops anything else writing it. Without this arm a
    /// <c>PATCH {"net_total": 1}</c> answered <c>200</c> and stored the <c>1</c>, every computed field reading the
    /// rollup followed it, and the forged total stood until some later child write happened to recompute it — a
    /// stored number that looks like data, the exact failure the rollup design exists to prevent.
    /// </para>
    /// <para>
    /// <b>The maintainer is unaffected because it is not a caller.</b> The recompute never passes through this
    /// type — it writes the column with its own statement, after the child row is stored — and a before-hook's
    /// patch is not re-judged here either (see <c>EfAlvoData.RunBeforeCreate</c>), so this refuses exactly the keys
    /// a caller sent and nothing the framework writes for itself.
    /// </para>
    /// <para>
    /// <b>The answer a caller sees is the computed field's, by design.</b> The port raises
    /// <see cref="AlvoAuthorizationException"/>, which the Data API renders as <c>403</c> with the problem type
    /// <c>forbidden</c> and this message as its <c>detail</c>; inside a batch it is a row refusal with the code
    /// <c>forbidden</c> at that row's pointer (<c>/rows/{index}</c>). An explicit <see langword="null"/> is
    /// refused too: it is a write, and the column's value is still not the caller's to choose. The message names
    /// the field and not the child entity it aggregates, which this caller may have no rule to read.
    /// </para>
    /// </remarks>
    private static string? RollupRefusal(IReadOnlyDictionary<string, object?> values, EntitySchema? entity)
    {
        var rollup = entity?.Fields
            .Where(field => field.Rollup is not null)
            .FirstOrDefault(field => values.ContainsKey(field.Name));

        return rollup is null
            ? null
            : $"Field '{rollup.Name}' is a rollup maintained by Alvo and cannot be written: it is recomputed from its "
            + "child rows inside every write to them, so a value sent here would be a stored total that is not the "
            + "children's. Remove it from the payload — its value follows from the rows it aggregates.";
    }

    /// <summary>
    /// Refuses every column the framework manages for this entity that a caller may not supply on this
    /// path. An entity the applied schema does not declare has already been refused by
    /// <see cref="QueryFieldGuard.EnsureDeclared"/>, so the row key is still covered.
    /// </summary>
    private static string? ManagedColumnRefusal(
        IReadOnlyDictionary<string, object?> values, EntitySchema? entity, bool isUpdate)
    {
        var refused = entity is null
            ? null
            : AlvoManagedColumns.For(entity)
                .Where(column => !AlvoManagedColumns.IsCallerWritable(column, isUpdate))
                .FirstOrDefault(values.ContainsKey);

        return refused is null ? null : $"Field '{refused}' {AlvoManagedColumns.RefusalReason(refused, isUpdate)}.";
    }

    private static string? ReadOnlyRefusal(
        IReadOnlyDictionary<string, object?> values, IReadOnlySet<string> readOnlyFields)
    {
        var refused = values.Keys.FirstOrDefault(readOnlyFields.Contains);

        return refused is null ? null : $"Field '{refused}' is read-only and cannot be written.";
    }
}
