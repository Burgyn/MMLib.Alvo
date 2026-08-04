using MMLib.Alvo.Descriptor.Internal;

namespace MMLib.Alvo.Tests.Descriptor;

/// <summary>
/// The <b>size and content</b> of the two unhonoured-feature tables, pinned as a Verify baseline.
/// </summary>
/// <remarks>
/// <para>
/// <b>This is the pin the table-driven theories structurally cannot be.</b> Every other fact about these
/// tables is driven <em>off</em> them — <c>Map_refuses_every_field_feature_the_table_records</c>,
/// <c>Every_unhonoured_field_feature_is_a_structured_error</c> — so removing an entry shrinks their own data
/// and they go on passing while the feature silently becomes accepted again. Measured: deleting
/// <c>"rollup"</c> from <see cref="UnhonouredFeatures.OnAField"/> left the whole project green.
/// </para>
/// <para>
/// <b>Why a baseline rather than a second hand-written list.</b> An in-test literal of the expected entries
/// would be a fifth copy of the very fact these tables exist to hold in one place — and it would drift for
/// exactly the reason the first four did. A Verify baseline is enforced by a mechanism the repository already
/// has: any addition or removal becomes a <em>reviewed baseline move</em> that fires the snapshot-judge turn
/// gate, so the change is judged by someone rather than merely compiled.
/// </para>
/// <para>
/// <b>The hook points also have a schema anchor, and that is deliberate overlap.</b>
/// <c>DescriptorValidatorTests.Every_hook_point_the_schema_declares_is_either_refused_or_honoured</c> asserts
/// the refused set against <c>project.schema.json</c> itself, which is a stronger statement than a baseline —
/// it says the set is <em>right</em>, not merely unchanged. The field-level table cannot be anchored that way
/// (most field properties are honoured, so there is no "exactly" to assert), which is why it needs this.
/// </para>
/// <para>
/// <b>The cost, stated.</b> The consequence and the fix are part of the snapshot, so improving a wording is a
/// baseline move too. That is the intended trade — these strings are the whole product of the refusal, and a
/// silent edit to one is how "names the consequence" decays back into "not supported yet" — but it does mean
/// this baseline moves more often than a purely structural one would.
/// </para>
/// <para>
/// Entries leave as features land, and three have: PR5a removed the three <c>after*</c> hook points it
/// implements, PR5b owns the three <c>before*</c> ones, PR6 owns <c>computed</c>, <c>rollup</c> and
/// <c>default</c>, soft delete leaves with its own implementation. Each of those is a deliberate baseline
/// move, which is the point.
/// </para>
/// </remarks>
public class UnhonouredFeaturesTests
{
    /// <summary>The action types the frozen <c>$defs/action</c> declares and this build never runs.</summary>
    private static readonly string[] _unrunnableActionTypes = ["function", "http.call", "entity.update"];

    [Fact]
    public Task Both_unhonoured_tables_are_pinned()
    {
        var tables = new
        {
            OnAField = UnhonouredFeatures.OnAField.Select(Describe).ToList(),
            OnAnEntity = UnhonouredFeatures.OnAnEntity.Select(Describe).ToList(),
        };

        return Verify(tables);
    }

    /// <summary>
    /// The third shape in the same file — the refusals a <b>compiler</b> detects rather than a descriptor-shape
    /// predicate — pinned the same way and for the same reason.
    /// </summary>
    /// <remarks>
    /// <para>
    /// These carry no path (the detection knows the exact JSON Pointer of the slot it is looking at, so the
    /// table holds only the words) and therefore no table-driven theory can be written over them at all. That
    /// makes the words <em>more</em> in need of a pin than the two tables above, not less: every fact about
    /// them asserts equality with this very property, which is right — one authority for the wording — and
    /// which means nothing else would notice the wording changing.
    /// </para>
    /// <para>
    /// A separate baseline rather than a fourth member of the one above, so that the two moves stay
    /// separable: <see cref="Both_unhonoured_tables_are_pinned"/> moves when a feature lands, this one moves
    /// when a refusal is reworded, and a reviewer reading either diff can tell which happened.
    /// </para>
    /// </remarks>
    [Fact]
    public Task Every_unhonoured_slot_is_pinned()
    {
        var slots = new
        {
            UnhonouredFeatures.RawJsonata,
            UnhonouredFeatures.EmailData,
            UnhonouredFeatures.TemplateBodyFile,
            Actions = _unrunnableActionTypes
                .Select(UnhonouredFeatures.UnhonouredAction)
                .ToList(),
        };

        return Verify(slots);
    }

    /// <summary>
    /// One entry as the baseline records it: its path and both halves of what it tells an author.
    /// </summary>
    /// <remarks>
    /// The predicate is deliberately <em>not</em> in the snapshot — a delegate has no stable rendering, and
    /// what it does is already asserted by the mapper's table-driven theory, which fails when a predicate
    /// stops matching. This pins what that theory cannot see: which entries exist.
    /// </remarks>
    /// <typeparam name="T">The descriptor the entry's predicate inspects.</typeparam>
    /// <param name="feature">The table entry.</param>
    private static object Describe<T>(UnhonouredFeature<T> feature) => new
    {
        feature.Path,
        feature.Consequence,
        feature.Fix,
    };
}
