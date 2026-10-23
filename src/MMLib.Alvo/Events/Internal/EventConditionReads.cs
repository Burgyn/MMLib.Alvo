using MMLib.Alvo.Expressions;
using MMLib.Alvo.Expressions.Internal;
using MMLib.Alvo.Rules;
using MMLib.Alvo.Rules.Internal;
using MMLib.Alvo.Schema;

namespace MMLib.Alvo.Events.Internal;

/// <summary>
/// What a compiled post-commit condition reads, for every kind of event subscriber — after-hooks and event handlers
/// alike — so they cannot drift on the fail-closed gate or on the <c>events.exclude</c> refusal.
/// </summary>
/// <remarks>
/// <c>@tenant.id</c> and <c>@user.roles</c> compile since #153, because the envelope carries <c>tenantid</c> and
/// <c>authclaims</c>. What an envelope may lack is a per-event fact, so the dispatcher refuses to <em>select</em> a
/// subscriber whose event does not carry what its condition reads — the same <see cref="RequiredContext"/> gate the
/// policy engine applies to a rule, because a negated comparison against an absent value passes every event.
/// </remarks>
internal static class EventConditionReads
{
    /// <summary>Which caller values <paramref name="condition"/> reads that an envelope may lack.</summary>
    /// <param name="condition">The compiled condition, or <see langword="null"/> for none.</param>
    internal static RequiredContext Required(CompiledExpression? condition) =>
        condition is null
            ? RequiredContext.None
            : new RequiredContext(
                TenantId: PolicyCatalogBuilder.ReferencesContextValue(condition.Root, CelContextValue.TenantId),
                UserId: PolicyCatalogBuilder.ReferencesContextValue(condition.Root, CelContextValue.UserId));

    /// <summary>Whether <paramref name="condition"/> reads <c>@user.roles</c>, which an envelope may lack.</summary>
    /// <param name="condition">The compiled condition, or <see langword="null"/> for none.</param>
    internal static bool ReadsRoles(CompiledExpression? condition) =>
        condition is not null && PolicyCatalogBuilder.ReferencesContextValue(condition.Root, CelContextValue.UserRoles);

    /// <summary>
    /// The fields <paramref name="root"/> reads — through <c>new.</c>, <c>old.</c>, a bare field or <c>has()</c> — that
    /// <paramref name="entity"/> leaves out of its event images, each once, in source order.
    /// </summary>
    /// <param name="root">The compiled condition's tree.</param>
    /// <param name="entity">The entity whose <c>events.exclude</c> applies.</param>
    internal static IEnumerable<string> ExcludedFieldsRead(CelNode root, EntitySchema entity) =>
        Excluded(FieldsRead(root), entity);

    /// <summary>
    /// <see cref="ExcludedFieldsRead"/>, and every excluded field a <c>changed(field)</c> call compares as well — the
    /// stricter reading an event handler is held to.
    /// </summary>
    /// <param name="root">The compiled condition's tree.</param>
    /// <param name="entity">The entity whose <c>events.exclude</c> applies.</param>
    internal static IEnumerable<string> ExcludedFieldsReadOrCompared(CelNode root, EntitySchema entity) =>
        Excluded(FieldsReadOrCompared(root), entity);

    private static IEnumerable<string> Excluded(IEnumerable<string> fields, EntitySchema entity) =>
        fields
            .Where(field => entity.EventImageExclusions.Contains(field, StringComparer.Ordinal))
            .Distinct(StringComparer.Ordinal);

    private static IEnumerable<string> FieldsRead(CelNode node) =>
        (node is CelFieldRef reference ? [reference.FieldName] : Enumerable.Empty<string>())
            .Concat(CelTree.Children(node).SelectMany(FieldsRead));

    private static IEnumerable<string> FieldsReadOrCompared(CelNode node) =>
        Own(node).Concat(CelTree.Children(node).SelectMany(FieldsReadOrCompared));

    private static string[] Own(CelNode node) => node switch
    {
        CelFieldRef reference => [reference.FieldName],
        CelChanged changed => [changed.FieldName],
        _ => [],
    };
}
