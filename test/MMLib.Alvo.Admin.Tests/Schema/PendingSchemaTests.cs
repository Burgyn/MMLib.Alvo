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

    [Fact]
    public void A_pending_entity_without_tenancy_is_scoped_when_the_project_turns_tenancy_on()
        => PendingSchema.Read("""{"tenancy":{"enabled":true},"entities":{"tickets":{"fields":{"t":{"type":"string"}}}}}""", "tickets")!
            .Tenancy.ShouldBe(TenancyMode.Scoped);

    [Fact]
    public void A_pending_entity_declared_global_stays_global_whatever_the_project_says()
        => PendingSchema.Read("""{"tenancy":{"enabled":true},"entities":{"tickets":{"tenancy":"global","fields":{"t":{"type":"string"}}}}}""", "tickets")!
            .Tenancy.ShouldBe(TenancyMode.Global);

    [Fact]
    public void A_pending_entity_without_tenancy_in_a_project_without_it_carries_none()
        => PendingSchema.Read("""{"entities":{"tickets":{"fields":{"t":{"type":"string"}}}}}""", "tickets")!
            .Tenancy.ShouldBeNull();

    [Fact]
    public void A_staged_field_keeps_its_index_its_default_and_its_nullability()
    {
        var field = PendingSchema.Read(
            """{"entities":{"t":{"fields":{"state":{"type":"string","index":true,"default":"open","nullable":false}}}}}""", "t")!
            .Fields.Single();

        field.Indexed.ShouldBeTrue();
        field.Default.ShouldNotBeNull().GetString().ShouldBe("open");
        field.Nullable.ShouldBeFalse();
    }

    [Theory]
    [InlineData("""{"type":"string","required":true}""", false)]
    [InlineData("""{"type":"string"}""", true)]
    public void Nullability_is_derived_from_required_when_it_is_not_declared(string declared, bool nullable)
        => PendingSchema.Read("""{"entities":{"t":{"fields":{"f":""" + declared + "}}}}", "t")!.Fields.Single()
            .Nullable.ShouldBe(nullable);

    [Fact]
    public void A_cel_default_is_not_a_literal_and_is_left_out_as_the_mapper_leaves_it_out()
        => PendingSchema.Read("""{"entities":{"t":{"fields":{"at":{"type":"datetime","default":{"$cel":"now()"}}}}}}""", "t")!
            .Fields.Single().Default.ShouldBeNull();
}
