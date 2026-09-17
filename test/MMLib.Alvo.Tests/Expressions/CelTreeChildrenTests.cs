using MMLib.Alvo.Expressions;
using MMLib.Alvo.Expressions.Internal;

namespace MMLib.Alvo.Tests.Expressions;

/// <summary>
/// <see cref="CelTree.Children"/> is the one place the node hierarchy's shape is enumerated, and every
/// whole-tree walker reads its children from it — the compiler's depth cap and the catalog builder's
/// role-literal check among them. A subtree missing from this list is a subtree no walker ever visits.
/// </summary>
/// <remarks>
/// <para>
/// <b>Every arm is asserted, and the set of arms is asserted too</b>, because the claim above is a claim
/// about the WHOLE hierarchy and two arms' worth of examples does not make it. The version of this file
/// that covered only <see cref="CelCall"/> left <see cref="CelHas"/> unpinned, and emptying that one arm
/// is a reachable authorization defect rather than a walking nicety:
/// <c>BeforeHookCompiler.Reads</c> recurses through this method, so a <c>beforeCreate</c> hook conditioned
/// on <c>has(old.title)</c> would stop being refused at apply, resolve <c>old.title</c> to null at request
/// time, answer <see langword="false"/>, and never fire — a deny rule silently inert. That end of it is
/// pinned where it belongs, in <c>BeforeHookCompilerTests</c>; what is pinned here is the walk.
/// </para>
/// <para>
/// Stryker generates no mutant for these collection-expression arms, so none of this moves a mutation
/// score. It is here because the consequence is real, not because a mutant survived.
/// </para>
/// </remarks>
public class CelTreeChildrenTests
{
    private static readonly CelFieldRef _title = new("title", CelValueType.String, CelRecordState.Current);
    private static readonly CelFieldRef _other = new("status", CelValueType.String, CelRecordState.Current);
    private static readonly CelLiteral _literal = new(CelValueType.String, "x");

    /// <summary>One entry per node kind: the node, and the children it must report, in order.</summary>
    /// <remarks>
    /// Order is part of the contract for the arms that have more than one child — a walker that reports a
    /// conditional's branches before its condition still visits every node, but a reader debugging a depth
    /// cap sees a tree that is not the one the author wrote.
    /// </remarks>
    private static readonly (string Kind, CelNode Node, CelNode[] Children)[] _everyKind =
    [
        (nameof(CelLiteral), _literal, []),
        (nameof(CelFieldRef), _title, []),
        (nameof(CelContextRef), new CelContextRef(CelContextValue.TenantId, CelValueType.Uuid), []),
        (nameof(CelChanged), new CelChanged("title"), []),
        (nameof(CelUnary), new CelUnary(CelUnaryOperator.Not, _title), [_title]),
        (nameof(CelBinary), new CelBinary(CelBinaryOperator.Equal, _title, _literal), [_title, _literal]),
        (nameof(CelConditional), new CelConditional(_title, _other, _literal), [_title, _other, _literal]),
        (nameof(CelHas), new CelHas(_title), [_title]),
        (nameof(CelCall), new CelCall(CelCall.LowerAscii, _title), [_title]),
    ];

    public static TheoryData<string, CelNode, CelNode[]> EveryKind()
    {
        var data = new TheoryData<string, CelNode, CelNode[]>();
        foreach (var (kind, node, children) in _everyKind)
        {
            data.Add(kind, node, children);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(EveryKind))]
    public void A_node_reports_exactly_its_own_subtree(string kind, CelNode node, CelNode[] children)
    {
        _ = kind;

        CelTree.Children(node).ShouldBe(children, ignoreOrder: false);
    }

    /// <summary>
    /// A nullary call reports nothing — the counterweight to the <see cref="CelCall"/> row above, without
    /// which an implementation that always yields the argument slot would pass.
    /// </summary>
    [Fact]
    public void A_nullary_call_reports_no_children()
        => CelTree.Children(new CelCall(CelCall.Now, null)).ShouldBeEmpty();

    /// <summary>
    /// And the theory above covers <b>every</b> kind the two assemblies define, which is the fact that makes
    /// this file a guard rather than a sample: a new <see cref="CelNode"/> kind added without a case in
    /// <see cref="CelTree.Children"/> fails here, at the one place that says so, instead of throwing from
    /// whichever walker reaches it first.
    /// </summary>
    [Fact]
    public void Every_node_kind_the_hierarchy_defines_is_covered_above()
    {
        var defined = new[] { typeof(CelNode).Assembly, typeof(CelTree).Assembly }
            .SelectMany(assembly => assembly.GetTypes())
            .Where(type => !type.IsAbstract && typeof(CelNode).IsAssignableFrom(type))
            .Select(type => type.Name)
            .OrderBy(name => name, StringComparer.Ordinal);

        var covered = _everyKind
            .Select(row => row.Kind)
            .OrderBy(name => name, StringComparer.Ordinal);

        defined.ShouldBe(covered);
    }
}
