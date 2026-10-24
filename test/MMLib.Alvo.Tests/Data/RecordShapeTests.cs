using MMLib.Alvo.Data;
using MMLib.Alvo.Data.Internal;

using FieldType = MMLib.Alvo.Schema.FieldType;

namespace MMLib.Alvo.Tests.Data;

/// <summary>How a host type maps to fields (spec 2026-10-14 §4.3): names, enum values, constructors, and the problems no descriptor cures.</summary>
public sealed class RecordShapeTests
{
    [Fact]
    public void A_property_maps_to_its_snake_case_name_unless_it_names_its_field()
    {
        var shape = RecordShape.Of(typeof(Order));

        Field(shape, nameof(Order.IsPaid)).ShouldBe("is_paid");
        Field(shape, nameof(Order.DueAt)).ShouldBe("due_at");
        Field(shape, nameof(Order.Reference)).ShouldBe("ref_no");
        shape.Entity.ShouldBe("orders");
        shape.Problems.ShouldBeEmpty();
    }

    [Fact]
    public void A_get_only_property_without_a_field_attribute_is_not_mapped() =>
        RecordShape.Of(typeof(Order)).Properties.Select(property => property.Property.Name).ShouldNotContain(nameof(Order.Summary));

    [Fact]
    public void Nullability_is_read_from_the_declaration()
    {
        var shape = RecordShape.Of(typeof(Order));

        Mapped(shape, nameof(Order.Code)).AcceptsNull.ShouldBeTrue();
        Mapped(shape, nameof(Order.Note)).AcceptsNull.ShouldBeFalse();
        Mapped(shape, nameof(Order.Rank)).AcceptsNull.ShouldBeTrue();
        Mapped(shape, nameof(Order.Rank)).ValueType.ShouldBe(typeof(int));
        Mapped(shape, nameof(Order.Total)).AcceptsNull.ShouldBeFalse();
    }

    [Fact]
    public void An_enum_member_maps_to_its_snake_case_name_unless_it_names_its_value()
    {
        var map = EnumMap.Of(typeof(OrderStatus));

        map.ValueOf(OrderStatus.InReview).ShouldBe("in_review");
        map.ValueOf(OrderStatus.Approved).ShouldBe("ok");
        map.MemberOf("draft").ShouldBe(OrderStatus.Draft);
        map.MemberOf("approved").ShouldBeNull();
    }

    [Fact]
    public void A_wrapper_is_built_from_its_record_and_maps_its_field_attributes()
    {
        var shape = RecordShape.Of(typeof(OrderView));

        shape.IsWrapper.ShouldBeTrue();
        shape.Constructor!.GetParameters().ShouldHaveSingleItem().ParameterType.ShouldBe(typeof(AlvoRecord));
        Field(shape, nameof(OrderView.Note)).ShouldBe("note");
        shape.Properties.Select(property => property.Property.Name).ShouldNotContain(nameof(OrderView.Record));
    }

    [Fact]
    public void A_positional_record_is_built_through_its_constructor() =>
        RecordShape.Of(typeof(OrderLine)).Constructor!.GetParameters().Length.ShouldBe(3);

    [Theory]
    [InlineData(typeof(TicketWithScore), "TicketWithScore.Score is a Double, which no field type holds.")]
    [InlineData(typeof(Uncallable), "Uncallable has no public constructor Alvo can call")]
    [InlineData(typeof(TwoNames), "TwoNames.Title and TwoNames.Heading map the same field 'title'.")]
    [InlineData(typeof(WithFlags), "Flagged is a [Flags] enum")]
    [InlineData(typeof(BadFieldName), "BadFieldName.Title maps to 'not a name', which is not a field name")]
    [InlineData(typeof(WithSharedValue), "SharedValue.Opened and SharedValue.Open map the same value 'open'.")]
    [InlineData(typeof(WrapperWithoutRecord), "WrapperWithoutRecord implements IAlvoEntity but has no public constructor taking an AlvoRecord.")]
    [InlineData(typeof(PrivatelySetNote), "PrivatelySetNote.Note is mapped, but Alvo cannot set it")]
    [InlineData(typeof(UncoveredNote), "UncoveredNote.Code is mapped, but Alvo cannot set it")]
    public void A_type_no_descriptor_can_map_carries_its_problem(Type type, string problem)
    {
        var problems = RecordShape.Of(type).Problems;

        problems.ShouldContain(candidate => candidate.Message.StartsWith(problem, StringComparison.Ordinal));
        problems.ShouldAllBe(candidate => !string.IsNullOrWhiteSpace(candidate.Fix));
    }

    [Fact]
    public void A_property_the_constructor_takes_is_not_a_problem_though_it_has_no_setter() =>
        RecordShape.Of(typeof(FixedNote)).Problems.ShouldBeEmpty();

    [Fact]
    public void An_override_of_a_named_property_is_mapped_to_the_base_field() =>
        Field(RecordShape.Of(typeof(NotedOverride)), nameof(NotedOverride.Text)).ShouldBe("note");

    [Fact]
    public void A_property_that_allows_null_on_write_accepts_null() =>
        Mapped(RecordShape.Of(typeof(LooseNote)), nameof(LooseNote.Note)).AcceptsNull.ShouldBeTrue();

    [Fact]
    public void A_get_only_property_takes_null_only_when_it_is_declared_nullable()
    {
        var shape = RecordShape.Of(typeof(FixedNote));

        Mapped(shape, nameof(FixedNote.Note)).AcceptsNull.ShouldBeFalse();
        Mapped(shape, nameof(FixedNote.Code)).AcceptsNull.ShouldBeTrue();
    }

    [Fact]
    public void A_text_property_on_an_integer_field_reads_with_its_article()
    {
        var entity = TypedFixtures.OrdersSchema with
        {
            Fields = [.. TypedFixtures.OrdersSchema.Fields.Select(field => field.Name == "note" ? field with { Type = FieldType.Integer } : field)],
        };

        ShapeMapping.Problems(RecordShape.Of(typeof(Order)), entity, fromEventImages: false).ShouldHaveSingleItem()
            .Message.ShouldBe("Order.Note is a String, but 'note' is an integer field, which holds Int64 or Int32.");
    }

    [Fact]
    public void The_constructor_arguments_and_the_setters_are_planned_once()
    {
        var line = RecordShape.Of(typeof(OrderLine));
        var order = RecordShape.Of(typeof(Order));

        line.Arguments.Select(property => property.Property.Name).ShouldBe([nameof(OrderLine.Id), nameof(OrderLine.Note), nameof(OrderLine.Total)]);
        line.Settable.ShouldBeEmpty();
        order.Arguments.ShouldBeEmpty();
        order.Settable.Count.ShouldBe(order.Properties.Count);
    }

    [Fact]
    public void An_abstract_type_is_refused_with_a_fix() =>
        RecordShape.Of(typeof(AbstractNote)).Problems.ShouldHaveSingleItem().ShouldSatisfyAllConditions(
            problem => problem.Message.ShouldBe("AbstractNote is abstract, so Alvo cannot create one."),
            problem => problem.Fix.ShouldBe("Map a concrete type that derives from AbstractNote."));

    [Fact]
    public void A_constructor_parameter_must_take_its_property_s_type() =>
        RecordShape.Of(typeof(MistypedParameter)).Constructor.ShouldBeNull();

    [Fact]
    public void An_enum_member_is_read_once_whatever_its_value()
    {
        var map = EnumMap.Of(typeof(OrderStatus));

        map.ShouldBeSameAs(EnumMap.Of(typeof(OrderStatus)));
        map.ValueOf(Enum.ToObject(typeof(OrderStatus), 1)).ShouldBe("in_review");
        map.ValueOf(99).ShouldBeNull();
        map.IsFlags.ShouldBeFalse();
    }

    [Fact]
    public void A_blank_field_name_is_refused_where_it_is_written() =>
        Should.Throw<ArgumentException>(() => new AlvoFieldAttribute(" "));

    [Fact]
    public void One_type_is_read_once() =>
        RecordShape.Of(typeof(Order)).ShouldBeSameAs(RecordShape.Of(typeof(Order)));

    [Fact]
    public void A_type_that_fits_its_entity_has_no_mapping_problem() =>
        ShapeMapping.Problems(RecordShape.Of(typeof(Order)), TypedFixtures.OrdersSchema, fromEventImages: true).ShouldBeEmpty();

    [Fact]
    public void A_property_whose_field_the_entity_lacks_is_named()
    {
        var entity = TypedFixtures.OrdersSchema with { Fields = [.. TypedFixtures.OrdersSchema.Fields.Where(field => field.Name != "ref_no")] };

        ShapeMapping.Problems(RecordShape.Of(typeof(Order)), entity, fromEventImages: false).ShouldHaveSingleItem()
            .Message.ShouldBe("Order.Reference maps to 'ref_no', which 'orders' does not declare.");
    }

    [Fact]
    public void A_property_of_another_type_than_its_field_holds_is_a_problem()
    {
        var entity = TypedFixtures.OrdersSchema with
        {
            Fields = [.. TypedFixtures.OrdersSchema.Fields.Select(field => field.Name == "quantity" ? field with { Type = FieldType.String } : field)],
        };

        ShapeMapping.Problems(RecordShape.Of(typeof(Order)), entity, fromEventImages: false).ShouldHaveSingleItem()
            .Message.ShouldBe("Order.Quantity is an Int64, but 'quantity' is a string field, which holds String.");
    }

    [Fact]
    public void A_field_left_out_of_the_event_images_is_a_problem_only_for_a_handler()
    {
        var entity = TypedFixtures.OrdersSchema with { EventImageExclusions = ["note"] };

        ShapeMapping.Problems(RecordShape.Of(typeof(Order)), entity, fromEventImages: false).ShouldBeEmpty();
        ShapeMapping.Problems(RecordShape.Of(typeof(Order)), entity, fromEventImages: true).ShouldHaveSingleItem()
            .Message.ShouldContain("leaves out of its event images (events.exclude)");
    }

    private static MappedProperty Mapped(RecordShape shape, string property) =>
        shape.Properties.Single(candidate => candidate.Property.Name == property);

    private static string Field(RecordShape shape, string property) => Mapped(shape, property).Field;
}
