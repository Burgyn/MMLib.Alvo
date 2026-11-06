using MMLib.Alvo.Expressions;
using MMLib.Alvo.Expressions.Internal;

using static MMLib.Alvo.Tests.Expressions.RelationFixtures;

namespace MMLib.Alvo.Tests.Expressions;

/// <summary>
/// A rule compiled with a relation catalog: a forward path and a reverse <c>exists</c> each become one
/// <see cref="CelRelation"/>, whose body reads the related row and reaches the outer row only through
/// <see cref="CelOuterFieldRef"/>; every shape that could widen access or read two relations at once is refused.
/// </summary>
public class CelRelationCompileTests
{
    private static readonly CelContextRef _userId = new(CelContextValue.UserId, CelValueType.Uuid);

    [Fact]
    public void A_forward_comparison_lifts_into_one_relation_whose_body_reads_the_related_row()
    {
        var relation = CompileRule("customer_id.owner_id == @user.id", Orders).Root.ShouldBeOfType<CelRelation>();

        relation.Kind.ShouldBe(CelRelationKind.Forward);
        relation.Related.Name.ShouldBe("customers");
        relation.RelatedKey.ShouldBe("id");
        relation.OuterKey.ShouldBe("customer_id");
        relation.Via.ShouldBe("customer_id");
        relation.Scope.ShouldBeNull();
        relation.Body.ShouldBe(new CelBinary(CelBinaryOperator.Equal, Inner("owner_id", CelValueType.Uuid), _userId));
    }

    [Fact]
    public void An_outer_field_beside_a_forward_path_becomes_an_outer_reference()
    {
        var relation = CompileRule("customer_id.region == region", Orders).Root.ShouldBeOfType<CelRelation>();

        relation.Body.ShouldBe(new CelBinary(
            CelBinaryOperator.Equal, Inner("region", CelValueType.String), new CelOuterFieldRef("region", CelValueType.String)));
    }

    [Fact]
    public void Two_forward_comparisons_on_one_ref_lift_separately_under_and_and_or()
    {
        var root = CompileRule("customer_id.active == true && (customer_id.region == 'eu' || title == 'x')", Orders).Root;

        var and = root.ShouldBeOfType<CelBinary>();
        and.Left.ShouldBeOfType<CelRelation>().Body.ShouldBe(
            new CelBinary(CelBinaryOperator.Equal, Inner("active", CelValueType.Bool), new CelLiteral(CelValueType.Bool, true)));
        var or = and.Right.ShouldBeOfType<CelBinary>();
        or.Left.ShouldBeOfType<CelRelation>().Via.ShouldBe("customer_id");
        or.Right.ShouldBe(new CelBinary(
            CelBinaryOperator.Equal, Inner("title", CelValueType.String), new CelLiteral(CelValueType.String, "x")));
    }

    [Fact]
    public void A_bare_boolean_path_is_a_relation_term_of_its_own()
    {
        var relation = CompileRule("customer_id.active", Orders).Root.ShouldBeOfType<CelRelation>();

        relation.Body.ShouldBe(Inner("active", CelValueType.Bool));
    }

    [Fact]
    public void A_reverse_exists_reads_the_child_through_its_single_reference()
    {
        var relation = CompileRule("project_members.exists(m, m.user_id == @user.id)", Projects).Root.ShouldBeOfType<CelRelation>();

        relation.Kind.ShouldBe(CelRelationKind.Reverse);
        relation.Related.Name.ShouldBe("project_members");
        relation.RelatedKey.ShouldBe("project_id");
        relation.OuterKey.ShouldBe("id");
        relation.Via.ShouldBe("project_members");
        relation.Body.ShouldBe(new CelBinary(CelBinaryOperator.Equal, Inner("user_id", CelValueType.Uuid), _userId));
    }

    [Fact]
    public void An_ambiguous_reverse_relation_is_refused_with_the_via_spellings()
    {
        var error = Compile("follows.exists(f, f.follower == @user.id)", People).Errors.ShouldHaveSingleItem();

        error.Message.ShouldBe(
            "'people' reaches 'follows', which references 'people' more than once, so which relationship to follow is "
            + "ambiguous. References from 'follows' to 'people': followee, follower.");
        error.FixSuggestion.ShouldBe(
            "Name the reference: follows_via_followee.exists(…) or follows_via_follower.exists(…).");
        error.Position.ShouldBe(0);
    }

    [Fact]
    public void A_via_spelling_resolves_the_ambiguous_reverse_relation()
    {
        var relation = CompileRule("follows_via_followee.exists(f, f.follower == @user.id)", People).Root.ShouldBeOfType<CelRelation>();

        relation.Related.Name.ShouldBe("follows");
        relation.RelatedKey.ShouldBe("followee");
        relation.Via.ShouldBe("follows_via_followee");
    }

    [Theory]
    [InlineData("!(customer_id.region == 'eu')", "orders")]
    [InlineData("title == 'x' && !(customer_id.active == true)", "orders")]
    [InlineData("!customer_id.active", "orders")]
    [InlineData("!project_members.exists(m, m.user_id == @user.id)", "projects")]
    [InlineData("!(owner_id == @user.id || project_members.exists(m, m.user_id == @user.id))", "projects")]
    public void A_relation_under_not_is_refused(string source, string entity)
    {
        var error = Compile(source, entity == "orders" ? Orders : Projects).Errors.ShouldHaveSingleItem();

        error.Message.ShouldBe(
            "A relation is negated here. A rule may only narrow as related rows become invisible, so a relation cannot sit under '!'.");
        error.FixSuggestion.ShouldBe("Say what must hold instead, or count the rows with a rollup and compare the count.");
    }

    [Fact]
    public void A_relation_compared_with_false_is_a_negation_and_is_refused()
    {
        var result = Compile("project_members.exists(m, m.user_id == @user.id) == false", Projects);

        result.IsSuccess.ShouldBeFalse();
    }

    [Theory]
    [InlineData("customer_id.active == false")]
    [InlineData("customer_id.region != 'eu'")]
    public void A_negative_comparison_inside_the_term_is_allowed(string source) =>
        CompileRule(source, Orders).Root.ShouldBeOfType<CelRelation>();

    [Fact]
    public void Has_over_an_outer_field_inside_a_body_reads_the_outer_row()
    {
        var relation = CompileRule("project_members.exists(m, has(note) && m.note == note)", Projects).Root.ShouldBeOfType<CelRelation>();

        var outerNote = new CelOuterFieldRef("note", CelValueType.String);
        relation.Body.ShouldBe(new CelBinary(
            CelBinaryOperator.And,
            new CelOuterHas(outerNote),
            new CelBinary(CelBinaryOperator.Equal, Inner("note", CelValueType.String), outerNote)));
    }

    [Fact]
    public void One_comparison_over_two_relations_is_refused()
    {
        var error = Compile("customer_id.region == project_id.note", Orders).Errors.ShouldHaveSingleItem();

        error.Message.ShouldBe("One comparison reads two relations ('customer_id', 'project_id').");
        error.FixSuggestion.ShouldBe("Compare each relation with a value or the caller, in its own clause.");
    }

    [Fact]
    public void Two_paths_through_one_ref_in_one_comparison_read_the_same_related_row()
    {
        var relation = CompileRule("customer_id.region == customer_id.note", Orders).Root.ShouldBeOfType<CelRelation>();

        relation.Body.ShouldBe(new CelBinary(
            CelBinaryOperator.Equal, Inner("region", CelValueType.String), Inner("note", CelValueType.String)));
    }

    [Theory]
    [InlineData("customer_id.owner_id.x == 1", "orders")]
    [InlineData("project_members.exists(m, m.project_id.owner_id == @user.id)", "projects")]
    [InlineData("project_members.exists(m, project_members.exists(n, n.user_id == @user.id))", "projects")]
    [InlineData("orders.exists(o, customer_id.region == 'eu')", "projects")]
    public void A_second_hop_or_a_nested_exists_is_refused(string source, string entity) =>
        Compile(source, entity == "orders" ? Orders : Projects).IsSuccess.ShouldBeFalse();

    [Fact]
    public void A_nested_exists_names_the_one_hop_rule()
    {
        var error = Compile("project_members.exists(m, orders.exists(o, o.title == 'x'))", Projects).Errors.ShouldHaveSingleItem();

        error.Message.ShouldBe(
            "'orders.exists(…)' reads a second relation inside 'project_members.exists(…)'; a rule term follows one relation.");
        error.FixSuggestion.ShouldBe(
            "Read the fields of 'm' only, and let 'project_members''s own list rule follow the next relation.");
    }

    [Fact]
    public void A_path_over_a_non_ref_is_refused_with_its_fix()
    {
        var error = Compile("title.x == 'a'", Orders).Errors.ShouldHaveSingleItem();

        error.Message.ShouldBe("'title' is not a reference on 'orders', so 'title.x' has nothing to follow.");
        error.FixSuggestion.ShouldBe(
            "Follow a 'ref' field of 'orders' (assignee_id, customer_id, note_id, project_id), or reach a child with <child>.exists(x, …).");
    }

    [Fact]
    public void A_path_over_a_missing_field_is_refused_as_not_a_reference()
    {
        var error = Compile("nope.x == 'a'", Orders).Errors.ShouldHaveSingleItem();

        error.Message.ShouldBe("'nope' is not a reference on 'orders', so 'nope.x' has nothing to follow.");
    }

    [Theory]
    [InlineData("assignee_id.name == 'x'", "'users' is the built-in users entity, so a rule cannot read its rows.", "assignee_id")]
    [InlineData("note_id.text == 'x'", "'notes' is dynamic (#41), so a rule cannot read its rows.", "note_id")]
    public void A_path_into_an_unreadable_entity_is_refused_with_its_fix(string source, string message, string reference)
    {
        var error = Compile(source, Orders).Errors.ShouldHaveSingleItem();

        error.Message.ShouldBe(message);
        error.FixSuggestion.ShouldBe($"Compare the ref itself, e.g. '{reference} == @user.id'.");
    }

    [Fact]
    public void A_path_to_a_field_the_related_entity_lacks_names_the_related_entity()
    {
        var error = Compile("customer_id.nope == 'a'", Orders).Errors.ShouldHaveSingleItem();

        error.Message.ShouldBe("'nope' is not a field of entity 'customers'.");
    }

    [Fact]
    public void An_exists_over_an_entity_that_is_no_child_is_refused()
    {
        var error = Compile("people.exists(p, p.name == 'x')", Projects).Errors.ShouldHaveSingleItem();

        error.Message.ShouldBe("'people' does not reference 'projects', so 'people.exists(…)' has no rows to read.");
        error.FixSuggestion.ShouldBe("Reach a child that references 'projects' (orders, project_members), or follow a 'ref' field with <ref>.<field>.");
    }

    [Fact]
    public void An_enum_literal_through_a_path_is_checked_against_the_related_field()
    {
        var error = Compile("customer_id.tier == 'bronze'", Orders).Errors.ShouldHaveSingleItem();

        error.Message.ShouldBe("'bronze' is not a declared value of this enum field.");
    }

    private static CelFieldRef Inner(string name, CelValueType type) => new(name, type, CelRecordState.Current);
}
