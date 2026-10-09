using MMLib.Alvo.Expressions;
using MMLib.Alvo.Expressions.Internal;

namespace MMLib.Alvo.Tests.Expressions;

/// <summary>The call node carries every argument and, once checked, its result type.</summary>
public sealed class CelCallShapeTests
{
    private static readonly CelFieldRef _title = new("title", CelValueType.String, CelRecordState.Current);

    [Fact]
    public void A_call_reports_every_argument_in_order()
    {
        var search = new CelLiteral(CelValueType.String, "a");
        var replacement = new CelLiteral(CelValueType.String, "b");

        CelTree.Children(new CelCall("replace", [_title, search, replacement])).ShouldBe([_title, search, replacement]);
    }

    [Fact]
    public void A_checked_call_carries_its_result_type()
    {
        ((CelCall)CelFixtures.CompileMutate("lowerAscii(title)").Root).ResultType.ShouldBe(CelValueType.String);
        ((CelCall)CelFixtures.CompileMutate("now()").Root).ResultType.ShouldBe(CelValueType.Timestamp);
    }

    [Fact]
    public void A_parsed_call_is_unchecked_until_the_type_checker_sees_it() =>
        ((CelCall)CelParser.Parse("now()")).ResultType.ShouldBe(CelValueType.Null);
}
