using MMLib.Alvo.Expressions;

using System.Collections.Concurrent;
using System.Runtime.CompilerServices;

namespace MMLib.Alvo.Rules.Internal;

/// <summary>
/// Binds every relation term of a rule to the related entity's <c>list</c> decision for one caller: its bound
/// <c>USING</c> and tenant scope, or <see cref="RelationScope.Deny"/> when that decision is a denial. A bound tree is
/// cached per catalog instance, so a re-apply starts empty.
/// </summary>
/// <remarks>
/// <para>
/// <b>The key holds every input a related decision's allow or deny depends on, and nothing that renders later.</b> The
/// entity, operation and slot name the tree; the depth caps the recursion; the presence of the caller's identity and
/// tenant decide the related tenant guard and required-context gate. Role membership is not in the key: a bound tree
/// still holds the related rule's <c>in @user.roles</c> node, and the renderer folds it per caller.
/// </para>
/// <para>
/// <b>The identity bit is exactly <see cref="RequiredContext.HasIdentity"/></b>, the gate's own test: the all-zero user
/// id is the anonymous caller, so an anonymous caller is never served a scope bound for an authenticated one.
/// </para>
/// </remarks>
/// <param name="resolveList">Resolves an entity's <c>list</c> decision over one catalog snapshot, at a depth.</param>
internal sealed class RelationBinder(Func<PolicyCatalog, string, AlvoContext, int, PolicyDecision> resolveList)
{
    private readonly ConditionalWeakTable<PolicyCatalog, BoundTrees> _bound = new();

    /// <summary>The expression with every relation bound for this caller, or the expression itself when it reads none.</summary>
    /// <param name="catalog">The one catalog snapshot this call resolves against.</param>
    /// <param name="entity">The entity the expression belongs to.</param>
    /// <param name="operation">The operation the expression decides.</param>
    /// <param name="slot">Which of the operation's expressions this is: <c>using</c> or <c>check</c>.</param>
    /// <param name="expression">The compiled expression, or <see langword="null"/>.</param>
    /// <param name="context">The caller.</param>
    /// <param name="depth">How many relations deep this decision already is.</param>
    internal CompiledExpression? Bind(
        PolicyCatalog catalog, string entity, DataOperation operation, string slot, CompiledExpression? expression,
        AlvoContext context, int depth) =>
        expression is null || !CelRewriter.HasRelation(expression.Root)
            ? expression
            : _bound.GetOrCreateValue(catalog).Expressions.GetOrAdd(
                KeyOf(entity, operation, slot, depth, context),
                key => new(BoundTree(catalog, key, expression.Root, context),
                    expression.Profile, expression.ResultType, expression.Source, expression.Entity));

    /// <summary>A lookup check, stored as its relation term, bound for this caller.</summary>
    /// <param name="catalog">The one catalog snapshot this call resolves against.</param>
    /// <param name="entity">The entity the check belongs to.</param>
    /// <param name="slot">The check's slot: <c>checks/&lt;index&gt;</c>.</param>
    /// <param name="check">The check's relation term.</param>
    /// <param name="context">The caller.</param>
    internal CelRelation Bind(PolicyCatalog catalog, string entity, string slot, CelRelation check, AlvoContext context) =>
        (CelRelation)_bound.GetOrCreateValue(catalog).Checks.GetOrAdd(
            KeyOf(entity, DataOperation.Create, slot, 0, context), key => BoundTree(catalog, key, check, context));

    private static BindingKey KeyOf(string entity, DataOperation operation, string slot, int depth, AlvoContext context) =>
        new(entity, operation, slot, depth, RequiredContext.HasIdentity(context), RequiredContext.HasTenant(context));

    private CelNode BoundTree(PolicyCatalog catalog, BindingKey key, CelNode root, AlvoContext context) =>
        CelRewriter.Replace(root, node =>
            node is CelRelation { Scope: null } relation
                ? relation with { Scope = Scope(catalog, relation, context, key.Depth) }
                : null);

    private RelationScope Scope(PolicyCatalog catalog, CelRelation relation, AlvoContext context, int depth)
    {
        if (depth >= RelationGraph.MaxDepth)
        {
            return RelationScope.Deny;
        }

        var related = resolveList(catalog, relation.Related.Name, context, depth + 1);
        return related.IsDenied ? RelationScope.Deny : new RelationScope(false, related.Using, related.TenantScope);
    }

    /// <summary>One catalog's bound expressions and bound checks.</summary>
    private sealed class BoundTrees
    {
        public ConcurrentDictionary<BindingKey, CompiledExpression> Expressions { get; } = new();

        public ConcurrentDictionary<BindingKey, CelNode> Checks { get; } = new();
    }

    /// <summary>One bound tree's identity: the inputs its related decisions depend on.</summary>
    private readonly record struct BindingKey(string Entity, DataOperation Operation, string Slot, int Depth, bool User, bool Tenant);
}
