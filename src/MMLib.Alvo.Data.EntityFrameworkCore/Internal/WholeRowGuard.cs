using MMLib.Alvo.Rules;
using MMLib.Alvo.Schema;

namespace MMLib.Alvo.Data.EntityFrameworkCore.Internal;

/// <summary>
/// The two rules that make a create-or-replace a <b>replacement</b> rather than a partial write: a field the
/// caller left out is written <see langword="null"/>, and a body that cannot express the whole row is refused.
/// </summary>
/// <remarks>
/// <para>
/// <b>Both rules live here rather than in the driver, because both belong to the port.</b> An embedded host
/// calling <c>IAlvoData.ReplaceAsync</c> directly must get the same answers as one coming through HTTP — a
/// completeness rule enforced only at the API layer is a courtesy, not a contract, and the two would drift
/// the first time either changed.
/// </para>
/// <para>
/// <b>Framework-managed columns are exempt from both.</b> A replaced row is the same row, so its
/// <c>created_at</c> and <c>created_by</c> are not the caller's to drop, and <c>id</c> and <c>tenant_id</c>
/// were never theirs to send. A <c>computed</c> or <c>rollup</c> field is exempt for a different reason: the
/// engine owns its value, so there is no write here to perform and no absence to complain about.
/// </para>
/// <para>
/// Composes no SQL and reads no row — every answer comes from the payload and the applied schema — so it
/// stays outside the SQL-composition allow-list and can be reasoned about without a database.
/// </para>
/// </remarks>
internal static class WholeRowGuard
{
    /// <summary>
    /// The payload as a whole row: every caller-owned field it does not mention written
    /// <see langword="null"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This is the one behaviour separating a replacement from a patch.</b> Keeping the stored value is
    /// what an update does by contract; doing it here would let two identical replacements applied to two
    /// differently-populated rows produce two different rows — and the caller could never clear a field.
    /// </para>
    /// <para>
    /// <b>A field this caller may not write, or may not read, is exempt — and that is a security rule, not a
    /// convenience.</b> <c>readOnly</c> is enforced by "did the payload name this field", so a field the
    /// policy froze is one the caller <em>cannot</em> name; nulling it here would destroy a frozen value
    /// through the one door the guard leaves open, and a frozen <c>owner_id</c> nulled this way changes which
    /// rows an ownership predicate matches. <c>hidden</c> is the same argument from the other side: a caller
    /// who cannot read a value cannot restate it, so treating its absence as a deletion punishes them for a
    /// mask they did not choose.
    /// </para>
    /// <para>
    /// <b>One decision, not the union of both branches' — and that is safe rather than an oversight.</b>
    /// Every other frozen-field check on this route takes both, because refusing under either is the
    /// default-deny reading. Here the two cannot differ: <c>PolicyEngine</c> resolves <c>hidden</c> and
    /// <c>readOnly</c> from entity-level masks over the caller's context, with no operation in scope, so a
    /// create decision and an update decision carry the same two sets. If either mask ever becomes
    /// per-operation, <b>this is the one site where divergence destroys a stored value rather than refusing a
    /// request</b>, and it must take the union first.
    /// </para>
    /// </remarks>
    /// <param name="values">The caller's payload, already stamped.</param>
    /// <param name="schema">The entity as the applied schema declares it.</param>
    /// <param name="decision">The caller's verdict, for the fields it froze and the fields it masks.</param>
    internal static IReadOnlyDictionary<string, object?> WholeRow(
        IReadOnlyDictionary<string, object?> values, EntitySchema schema, PolicyDecision decision)
    {
        ArgumentNullException.ThrowIfNull(values);
        ArgumentNullException.ThrowIfNull(schema);
        ArgumentNullException.ThrowIfNull(decision);

        var whole = new Dictionary<string, object?>(values, StringComparer.Ordinal);
        foreach (var field in CallerOwnedFields(schema, decision))
        {
            if (!whole.ContainsKey(field.Name))
            {
                whole[field.Name] = null;
            }
        }

        return whole;
    }

    /// <summary>Refuses a payload that cannot express the whole row, naming the field it left out.</summary>
    /// <remarks>
    /// <para>
    /// <b>Refused rather than merged, and on both branches.</b> Accepting it by keeping the stored value
    /// would reintroduce exactly the merge <see cref="WholeRow"/> exists to remove — arriving through the one
    /// field where the caller could never undo it. Checking only the branch with a stored row would be worse
    /// still: a caller could write a half row simply by naming an unused id.
    /// </para>
    /// <para>
    /// <b><see cref="ArgumentException"/>, not <see cref="AlvoAuthorizationException"/></b>: nothing was
    /// refused by a policy and no row was consulted, so this is the port's broken-caller family and the
    /// HTTP layer renders it <c>422</c> rather than <c>403</c>.
    /// </para>
    /// <para>
    /// <b>The message names the <c>required</c> + <c>hidden</c> case</b>, because it is the one a caller
    /// cannot fix by editing their body: a mandatory secret they may write and never read back cannot be
    /// restated, so for them the entity is reachable only through a partial update. Saying so is the
    /// difference between an error they can act on and one that reads as a bug.
    /// </para>
    /// <para>
    /// <b>The mask exemptions <see cref="WholeRow"/> applies do <em>not</em> apply here, and that asymmetry is
    /// the whole of this method.</b> The two ask different questions. "May this field's absence mean delete
    /// it?" is answered <see langword="no"/> for a frozen or masked field, because the caller could not have
    /// named it. "May this row be stored without it?" is answered by the column, which does not care who was
    /// allowed to read it — so exempting them here would let the create branch insert a row missing a
    /// <c>NOT NULL</c> column and hand an embedded caller the engine's own violation instead of this
    /// sentence. It would also make the sentence unreachable, since the check that emits it would have
    /// excluded exactly the fields it describes.
    /// </para>
    /// </remarks>
    /// <param name="values">The caller's payload.</param>
    /// <param name="schema">The entity as the applied schema declares it.</param>
    /// <exception cref="ArgumentException">A <c>required</c> field is missing from <paramref name="values"/>.</exception>
    internal static void EnsureWholeRow(IReadOnlyDictionary<string, object?> values, EntitySchema schema)
    {
        ArgumentNullException.ThrowIfNull(values);
        ArgumentNullException.ThrowIfNull(schema);

        var missing = DeclaredFields(schema)
            .FirstOrDefault(field => MustBeSupplied(field) && IsUnsupplied(values, field.Name));

        if (missing is not null)
        {
            throw new ArgumentException(MissingFieldReason(missing.Name), nameof(values));
        }
    }

    /// <summary>The refusal's wording, shared so both implementations of the port answer identically.</summary>
    /// <param name="field">The field the payload left out.</param>
    internal static string MissingFieldReason(string field) =>
        $"Field '{field}' cannot be null and this write replaces the whole row, so leaving it out would store "
        + "no value for it. Supply it, or use a partial update instead. A field that is both required and "
        + "hidden cannot be supplied by a caller who cannot read it, which makes this entity replaceable "
        + "only through a partial update for them.";

    /// <summary>Whether the store will refuse this field's absence.</summary>
    /// <remarks>
    /// <b><c>required</c> is not the only way a column says no.</b> A field declared neither <c>required</c>
    /// nor <c>nullable</c> maps to a <c>NOT NULL</c> column all the same, so a replacement that omitted it
    /// would write <see langword="null"/> and be refused by the engine — a 500 carrying the provider's own
    /// wording where the caller should have been told which field to supply.
    /// </remarks>
    /// <param name="field">The field as the applied schema declares it.</param>
    private static bool MustBeSupplied(FieldSchema field) => field.Required || !field.Nullable;

    /// <summary>
    /// Whether the payload leaves <paramref name="field"/> unsaid — absent, or present as an explicit
    /// <see langword="null"/>.
    /// </summary>
    /// <remarks>
    /// <b>An explicit <c>null</c> counts as unsaid for a <c>required</c> field</b>, because the row it asks
    /// for is the same row an omission asks for and the store refuses both. Testing only for the key would
    /// let <c>{"rank": null}</c> through the port and die on the column's own <c>NOT NULL</c> — a 500 where
    /// the caller should have been told which field to supply, and exactly the parity with the HTTP layer
    /// this type exists to keep.
    /// </remarks>
    /// <param name="values">The caller's payload.</param>
    /// <param name="field">The field's name.</param>
    private static bool IsUnsupplied(IReadOnlyDictionary<string, object?> values, string field) =>
        !values.TryGetValue(field, out var value) || value is null;

    /// <summary>
    /// The fields a replacement <b>may clear</b>: declared, not framework-managed, not engine-computed, and
    /// neither frozen nor masked for this caller.
    /// </summary>
    /// <param name="schema">The entity as the applied schema declares it.</param>
    /// <param name="decision">The caller's verdict.</param>
    private static IEnumerable<FieldSchema> CallerOwnedFields(EntitySchema schema, PolicyDecision decision) =>
        DeclaredFields(schema).Where(field =>
            !decision.ReadOnlyFields.Contains(field.Name)
            && !decision.HiddenFields.Contains(field.Name));

    /// <summary>
    /// The fields a row's own shape is made of: declared, not framework-managed, not engine-computed.
    /// </summary>
    /// <remarks>
    /// Deliberately blind to the caller's masks. Whether a row can be <em>stored</em> without a field is a
    /// question for the column, and a caller's read or write permission does not change the answer.
    /// </remarks>
    /// <param name="schema">The entity as the applied schema declares it.</param>
    private static IEnumerable<FieldSchema> DeclaredFields(EntitySchema schema)
    {
        var managed = AlvoManagedColumns.For(schema);
        return schema.Fields.Where(field =>
            !managed.Contains(field.Name) && field.ComputedExpression is null && field.Rollup is null);
    }
}
