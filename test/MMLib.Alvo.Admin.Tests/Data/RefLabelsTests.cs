using MMLib.Alvo.Admin.Components.Data;
using MMLib.Alvo.Data;
using MMLib.Alvo.Schema;

namespace MMLib.Alvo.Admin.Tests.Data;

/// <summary>
/// The ids of a reference column, as the reads that fetch their labels.
/// </summary>
public class RefLabelsTests
{
    private static readonly Guid _bike = Guid.Parse("0199a1b2-c3d4-7e5f-8a9b-0c1d2e3f4a5b");
    private static readonly Guid _other = Guid.Parse("0199a1b2-0000-7e5f-8a9b-0c1d2e3f4a5c");

    [Fact]
    public void A_page_of_references_is_one_batch_of_distinct_ids()
    {
        var batches = RefLabels.Batches([_bike, _other.ToString(), _bike, null, "not-a-uuid"]);

        batches.ShouldHaveSingleItem().ShouldBe([_bike, _other]);
    }

    [Fact]
    public void A_batch_never_exceeds_what_an_in_filter_accepts()
    {
        var batches = RefLabels.Batches(
            [.. Enumerable.Range(0, AlvoFilter.MaxInCandidates + 1).Select(_ => (object?)Guid.NewGuid())]);

        batches.Select(batch => batch.Count).ShouldBe([AlvoFilter.MaxInCandidates, 1]);
    }

    [Fact]
    public void No_ids_is_no_read()
        => RefLabels.Batches([null, null]).ShouldBeEmpty();

    [Fact]
    public void The_read_is_an_in_filter_projected_to_the_id_and_the_label()
    {
        var query = RefLabels.Query("customers", new RowLabel(["first_name", "last_name"]), [_bike, _other]);

        query.Entity.ShouldBe("customers");
        var filter = query.Filter.ShouldBeOfType<AlvoComparison>();
        filter.Field.ShouldBe("id");
        filter.Operator.ShouldBe(AlvoFilterOperator.In);
        filter.Value.ShouldBeAssignableTo<IEnumerable<Guid>>()!.ShouldBe([_bike, _other]);
        query.Select.ShouldBe(["id", "first_name", "last_name"]);
        query.Limit.ShouldBe(2);
        AlvoFilter.EnsureWithinLimits(query.Filter);
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData("   ", null)]
    [InlineData("Ada Lovelace", "Ada Lovelace")]
    public void A_missing_or_blank_label_is_no_label(string? stored, string? label)
        => RefLabels.Label(stored).ShouldBe(label);

    [Fact]
    public void A_short_id_is_the_first_eight_characters()
        => RefLabels.ShortId(_bike).ShouldBe("0199a1b2");

    [Theory]
    [InlineData("name")]
    [InlineData("title")]
    [InlineData("label")]
    [InlineData("display_name")]
    [InlineData("reference")]
    [InlineData("code")]
    [InlineData("sku")]
    [InlineData("order_number")]
    public void A_name_like_field_is_the_label_before_the_first_required_string(string named)
    {
        var entity = Entity(
            Strings("brand", required: true),
            Strings("colour"),
            Strings(named));

        RefLabels.For(entity, FieldMasks.None)!.Fields.ShouldBe([named]);
    }

    [Fact]
    public void The_name_like_fields_are_tried_in_their_order()
        => RefLabels.For(
                Entity(Strings("order_number"), Strings("sku"), Strings("title"), Strings("name")), FieldMasks.None)!
            .Fields.ShouldBe(["name"]);

    [Fact]
    public void First_and_last_name_together_are_the_label()
    {
        var label = RefLabels.For(
            Entity(Strings("email", required: true), Strings("last_name"), Strings("first_name")), FieldMasks.None)!;

        label.Fields.ShouldBe(["first_name", "last_name"]);
        label.Of(Row(("first_name", "Ada"), ("last_name", "Lovelace"))).ShouldBe("Ada Lovelace");
        label.Of(Row(("last_name", "Lovelace"))).ShouldBe("Lovelace", "a masked or unset half is skipped");
        label.Of(Row()).ShouldBeNull();
    }

    [Fact]
    public void A_first_name_alone_is_not_a_composite()
        => RefLabels.For(Entity(Strings("phone", required: true), Strings("first_name")), FieldMasks.None)!
            .Fields.ShouldBe(["phone"]);

    [Fact]
    public void Otherwise_the_label_is_the_first_required_string_then_the_first_string()
    {
        RefLabels.For(Entity(Strings("nickname"), Strings("brand", required: true)), FieldMasks.None)!
            .Fields.ShouldBe(["brand"]);
        RefLabels.For(Entity(Strings("nickname"), Strings("colour")), FieldMasks.None)!
            .Fields.ShouldBe(["nickname"]);
    }

    [Fact]
    public void An_entity_with_no_string_has_no_label()
        => RefLabels.For(
                Entity(
                    new FieldSchema { Name = "amount", Type = FieldType.Decimal },
                    new FieldSchema { Name = "name", Type = FieldType.Text }),
                FieldMasks.None)
            .ShouldBeNull("a text field is prose, not a name, whatever it is called");

    [Fact]
    public void A_field_hidden_from_everyone_is_never_the_label()
        => RefLabels.For(Entity(Strings("name"), Strings("brand", required: true)), Masks(always: ["name"]))!
            .Fields.ShouldBe(["brand"], "a field that never comes back would label every row with the short id");

    [Fact]
    public void A_field_masked_by_cel_can_still_be_the_label()
        => RefLabels.For(Entity(Strings("name")), Masks(conditional: ["name"]))!
            .Fields.ShouldBe(["name"], "it comes back for some callers, and the others get the short id");

    /// <summary>A label field a CEL mask may withhold is read out, never searched or sorted by (final review M-2).</summary>
    [Fact]
    public void A_field_masked_by_cel_is_not_one_the_label_is_searched_by()
    {
        var label = RefLabels.For(
            Entity(Strings("first_name"), Strings("last_name")), Masks(conditional: ["last_name"]))!;

        label.Fields.ShouldBe(["first_name", "last_name"]);
        label.Searchable.ShouldBe(["first_name"], "the port refuses the whole search for a caller the mask hides it from");
    }

    [Fact]
    public void An_unmasked_label_is_searched_by_every_field_it_reads()
        => RefLabels.For(Entity(Strings("first_name"), Strings("last_name")), FieldMasks.None)!
            .Searchable.ShouldBe(["first_name", "last_name"]);

    [Fact]
    public void A_managed_column_is_never_the_label()
        => RefLabels.For(Entity(Strings("created_by"), Strings("brand")) with { Audit = true }, FieldMasks.None)!
            .Fields.ShouldBe(["brand"]);

    private static FieldMasks Masks(string[]? always = null, string[]? conditional = null)
        => new(
            new HashSet<string>(always ?? [], StringComparer.Ordinal),
            new HashSet<string>(conditional ?? [], StringComparer.Ordinal));

    private static EntitySchema Entity(params FieldSchema[] fields) => new() { Name = "bikes", Fields = fields };

    private static FieldSchema Strings(string name, bool required = false)
        => new() { Name = name, Type = FieldType.String, Required = required };

    private static AlvoRecord Row(params (string Field, object? Value)[] values)
        => new(values.ToDictionary(value => value.Field, value => value.Value));
}
