using MMLib.Alvo.Admin.Components.Schema;
using MMLib.Alvo.Schema;

namespace MMLib.Alvo.Admin.Tests.Schema;

/// <summary>
/// The one table the guided condition offers from, the generator writes from, the recognizer reads with and the Host.Tests
/// conformance fact compiles (spec §7.1).
/// </summary>
public class ConditionTableTests
{
    [Theory]
    [InlineData(FieldType.String, ConditionFieldKind.Text)]
    [InlineData(FieldType.Text, ConditionFieldKind.Text)]
    [InlineData(FieldType.Enum, ConditionFieldKind.Choice)]
    [InlineData(FieldType.Integer, ConditionFieldKind.Number)]
    [InlineData(FieldType.Decimal, ConditionFieldKind.Number)]
    [InlineData(FieldType.Boolean, ConditionFieldKind.Flag)]
    [InlineData(FieldType.Date, ConditionFieldKind.Moment)]
    [InlineData(FieldType.DateTime, ConditionFieldKind.Moment)]
    [InlineData(FieldType.Uuid, ConditionFieldKind.Identity)]
    [InlineData(FieldType.Ref, ConditionFieldKind.Identity)]
    [InlineData(FieldType.Json, ConditionFieldKind.Json)]
    public void A_field_type_has_one_kind(FieldType type, object kind) => ConditionTable.KindOf(type).ShouldBe((ConditionFieldKind)kind);

    /// <summary>The rows whose CEL names no image are exactly the ones the form asks no image for (review Minor 5).</summary>
    [Fact]
    public void Only_changed_and_the_role_rows_read_no_image()
        => ConditionTable.Rows.Where(spec => !spec.ReadsImage).Select(spec => spec.Operator)
            .ShouldBe([ConditionOperator.Changed, ConditionOperator.HasRole, ConditionOperator.LacksRole]);

    [Fact]
    public void A_row_reads_an_image_exactly_when_its_format_names_one()
        => ConditionTable.Rows.ShouldAllBe(spec => spec.ReadsImage == spec.Format.Contains("{f}", StringComparison.Ordinal));

    [Fact]
    public void Every_declared_field_type_has_a_kind()
        => Enum.GetValues<FieldType>().ShouldAllBe(type => Enum.IsDefined(ConditionTable.KindOf(type)));

    [Fact]
    public void A_field_type_the_table_does_not_know_is_refused_rather_than_guessed()
        => Should.Throw<ArgumentOutOfRangeException>(() => ConditionTable.KindOf((FieldType)999));

    [Fact]
    public void A_point_is_compared_ordinally_by_the_before_and_update_readers()
    {
        HookBuilder.IsBefore("BeforeCreate").ShouldBeFalse();
        ConditionTable.IsUpdate("beforeupdate").ShouldBeFalse();
        HookBuilder.IsBefore("beforeCreate").ShouldBeTrue();
        ConditionTable.IsUpdate("afterUpdate").ShouldBeTrue();
    }

    [Theory]
    [InlineData("beforeCreate", new[] { RowImage.New })]
    [InlineData("beforeUpdate", new[] { RowImage.New, RowImage.Old })]
    [InlineData("beforeDelete", new[] { RowImage.Old })]
    [InlineData("afterCreate", new[] { RowImage.New })]
    [InlineData("afterUpdate", new[] { RowImage.New, RowImage.Old })]
    [InlineData("afterDelete", new[] { RowImage.Old })]
    public void A_point_offers_only_the_images_it_has(string point, object images)
        => ConditionTable.ImagesAt(point).ShouldBe((RowImage[])images);

    [Theory]
    [InlineData("beforeUpdate", true)]
    [InlineData("afterUpdate", true)]
    [InlineData("beforeCreate", false)]
    [InlineData("beforeDelete", false)]
    [InlineData("afterCreate", false)]
    [InlineData("afterDelete", false)]
    public void Changed_is_offered_only_where_a_write_has_two_images(string point, bool offered)
        => ConditionTable.For(point, ConditionFieldKind.Text, nullable: false)
            .Any(spec => spec.Operator == ConditionOperator.Changed).ShouldBe(offered);

    [Theory]
    [InlineData("beforeCreate", true)]
    [InlineData("beforeUpdate", true)]
    [InlineData("beforeDelete", true)]
    [InlineData("afterCreate", false)]
    [InlineData("afterUpdate", false)]
    [InlineData("afterDelete", false)]
    public void A_role_row_is_offered_only_before_the_commit(string point, bool offered)
        => (ConditionTable.ForWriter(point).Count > 0).ShouldBe(offered);

    [Fact]
    public void The_operators_that_admit_an_empty_value_are_offered_only_on_a_nullable_field()
    {
        var required = ConditionTable.For("beforeUpdate", ConditionFieldKind.Text, nullable: false).Select(spec => spec.Operator).ToList();
        var optional = ConditionTable.For("beforeUpdate", ConditionFieldKind.Text, nullable: true).Select(spec => spec.Operator).ToList();

        required.ShouldNotContain(ConditionOperator.IsNotOrEmpty);
        required.ShouldNotContain(ConditionOperator.HasValue);
        required.ShouldNotContain(ConditionOperator.IsEmpty);
        optional.ShouldContain(ConditionOperator.IsNotOrEmpty);
        optional.ShouldContain(ConditionOperator.HasValue);
        optional.ShouldContain(ConditionOperator.IsEmpty);
    }

    [Fact]
    public void A_moment_takes_no_literal_and_a_json_field_only_presence()
    {
        ConditionTable.For("beforeUpdate", ConditionFieldKind.Moment, nullable: true).ShouldAllBe(spec => spec.Operand == OperandKind.None);
        ConditionTable.For("beforeUpdate", ConditionFieldKind.Json, nullable: true).Select(spec => spec.Operator)
            .ShouldBe([ConditionOperator.HasValue, ConditionOperator.IsEmpty]);
    }

    [Fact]
    public void Relational_operators_are_offered_on_numbers_only()
    {
        ConditionTable.For("beforeCreate", ConditionFieldKind.Text, nullable: true).ShouldNotContain(spec => spec.Operator == ConditionOperator.Less);
        ConditionTable.For("beforeCreate", ConditionFieldKind.Number, nullable: false).ShouldContain(spec => spec.Operator == ConditionOperator.Less);
    }

    [Theory]
    [InlineData(ConditionOperator.StartsWith)]
    [InlineData(ConditionOperator.EndsWith)]
    [InlineData(ConditionOperator.Contains)]
    public void A_text_test_is_offered_for_text_only_at_every_image_point(object relation)
    {
        var offered = (ConditionOperator)relation;
        ConditionTable.Of(offered).RefusesEmpty.ShouldBeTrue("every text passes a text test with an empty value");
        foreach (var point in HookBuilder.Points.Where(point => ConditionTable.ImagesAt(point).Count > 0))
        {
            ConditionTable.For(point, ConditionFieldKind.Text, nullable: true).ShouldContain(spec => spec.Operator == offered);
            ConditionTable.For(point, ConditionFieldKind.Text, nullable: false).ShouldContain(spec => spec.Operator == offered);
            foreach (var kind in Enum.GetValues<ConditionFieldKind>().Where(kind => kind != ConditionFieldKind.Text))
            {
                ConditionTable.For(point, kind, nullable: true).ShouldNotContain(spec => spec.Operator == offered, kind.ToString());
                ConditionTable.For(point, kind, nullable: false).ShouldNotContain(spec => spec.Operator == offered, kind.ToString());
            }
        }
    }

    [Fact]
    public void Only_a_text_test_refuses_an_empty_value()
        => ConditionTable.Rows.Where(spec => spec.RefusesEmpty).Select(spec => spec.Format)
            .ShouldAllBe(format => format.EndsWith("({f}, {v})", StringComparison.Ordinal));

    [Fact]
    public void Every_format_carries_exactly_the_placeholder_its_operand_needs()
    {
        foreach (var spec in ConditionTable.Rows)
        {
            spec.Format.Contains("{v}", StringComparison.Ordinal).ShouldBe(spec.Operand == OperandKind.Literal, spec.Operator.ToString());
            spec.Format.Contains("{r}", StringComparison.Ordinal).ShouldBe(spec.Operand == OperandKind.Role, spec.Operator.ToString());
        }
    }

    [Fact]
    public void Every_operator_has_exactly_one_row()
        => ConditionTable.Rows.Select(spec => spec.Operator).ShouldBe(Enum.GetValues<ConditionOperator>(), ignoreOrder: true);

    [Theory]
    [InlineData("beforeCreate", "<code class=\"a-mono\">new</code>", "new.priority == 'high'")]
    [InlineData("beforeUpdate", "<code class=\"a-mono\">new</code>, <code class=\"a-mono\">old</code>", "new.status == 'completed'")]
    [InlineData("beforeDelete", "<code class=\"a-mono\">old</code>", "old.status == 'completed'")]
    [InlineData("afterCreate", "<code class=\"a-mono\">new</code>", "new.status == 'completed'")]
    [InlineData("afterUpdate", "<code class=\"a-mono\">new</code>, <code class=\"a-mono\">old</code>", "new.status == 'completed'")]
    [InlineData("afterDelete", "<code class=\"a-mono\">old</code>", "old.status == 'completed'")]
    public void The_hint_and_the_example_name_only_the_images_the_point_has(string point, string images, string example)
    {
        ConditionTable.ImagesMarkup(point).ShouldBe(images);
        ConditionTable.Example(point).ShouldBe(example);
    }
}
