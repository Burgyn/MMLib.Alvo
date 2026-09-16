using MMLib.Alvo.Expressions;
using MMLib.Alvo.Expressions.Internal;

namespace MMLib.Alvo.Tests.Expressions;

/// <summary>
/// <see cref="CelTree.Children"/> is the one place the node hierarchy's shape is enumerated, and every
/// whole-tree walker reads its children from it — the compiler's depth cap and the catalog builder's
/// role-literal check among them. A subtree missing from this list is a subtree no walker ever visits.
/// </summary>
public class CelTreeChildrenTests
{
    /// <summary>
    /// A call's argument is part of its subtree. Reported as no children, the depth cap would measure a
    /// call as a leaf and the role-literal walk would never see a literal underneath one.
    /// </summary>
    [Fact]
    public void A_call_reports_its_argument_as_a_child()
    {
        var argument = new CelFieldRef("title", CelValueType.String, CelRecordState.Current);

        CelTree.Children(new CelCall(CelCall.LowerAscii, argument)).ShouldHaveSingleItem().ShouldBe(argument);
    }

    [Fact]
    public void A_nullary_call_reports_no_children()
        => CelTree.Children(new CelCall(CelCall.Now, null)).ShouldBeEmpty();
}
