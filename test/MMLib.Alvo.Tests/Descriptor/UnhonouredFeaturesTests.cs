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
/// <c>DescriptorValidatorTests.The_unhonoured_table_covers_every_hook_point_the_schema_declares</c> asserts
/// the six against <c>project.schema.json</c> itself, which is a stronger statement than a baseline — it says
/// the set is <em>right</em>, not merely unchanged. The field-level table cannot be anchored that way (most
/// field properties are honoured, so there is no "exactly" to assert), which is why it needs this.
/// </para>
/// <para>
/// <b>The cost, stated.</b> The consequence and the fix are part of the snapshot, so improving a wording is a
/// baseline move too. That is the intended trade — these strings are the whole product of the refusal, and a
/// silent edit to one is how "names the consequence" decays back into "not supported yet" — but it does mean
/// this baseline moves more often than a purely structural one would.
/// </para>
/// <para>
/// Entries leave as features land: PR5 removes the hook points it implements, PR6 owns <c>computed</c>,
/// <c>rollup</c> and <c>default</c>, soft delete leaves with its own implementation. Each of those is a
/// deliberate baseline move, which is the point.
/// </para>
/// </remarks>
public class UnhonouredFeaturesTests
{
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
