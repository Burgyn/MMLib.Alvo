using MMLib.Alvo.Admin.Components.Schema;
using MMLib.Alvo.Schema;

namespace MMLib.Alvo.Admin.Tests.Schema;

/// <summary>A staged field is drawn with the facets an applied one would be (docs/todo-admin.md §8d item 22).</summary>
public class PendingSchemaTests
{
    [Fact]
    public void A_staged_rollup_and_computed_keep_what_makes_them_one()
    {
        const string json = """
            {"entities":{"orders":{"fields":{
              "lines_count":{"type":"integer","rollup":{"from":"lines","op":"count"}},
              "twice":{"type":"integer","computed":"a + a"}}}}}
            """;
        var fields = PendingSchema.Read(json, "orders")!.Fields;

        fields[0].Rollup.ShouldNotBeNull().From.ShouldBe("lines");
        fields[0].Rollup!.Op.ShouldBe(RollupOperation.Count);
        fields[1].ComputedExpression.ShouldBe("a + a");
    }
}
