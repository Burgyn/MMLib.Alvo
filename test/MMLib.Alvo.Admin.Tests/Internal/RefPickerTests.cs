using MMLib.Alvo.Admin.Internal;
using MMLib.Alvo.Data;
using MMLib.Alvo.Schema;

namespace MMLib.Alvo.Admin.Tests.Internal;

/// <summary>
/// The record form's reference control: the read a search makes, and what its rows read as.
/// </summary>
public class RefPickerTests
{
    private static readonly Guid _ada = Guid.Parse("0199a1b2-c3d4-7e5f-8a9b-0c1d2e3f4a5b");
    private static readonly Guid _grace = Guid.Parse("0199a1b2-0000-7e5f-8a9b-0c1d2e3f4a5c");

    [Fact]
    public void A_search_is_the_grids_ilike_over_the_label_limited_to_twenty()
    {
        var query = Customers(new RowLabel(["name"])).Query("ada");

        query.Entity.ShouldBe("customers");
        var filter = query.Filter.ShouldBeOfType<AlvoOr>().Filters.ShouldHaveSingleItem().ShouldBeOfType<AlvoComparison>();
        filter.Field.ShouldBe("name");
        filter.Operator.ShouldBe(AlvoFilterOperator.ILike);
        filter.Value.ShouldBe("%ada%");
        query.Select.ShouldBe(["id", "name"]);
        query.Limit.ShouldBe(RefPicker.Limit);
        AlvoFilter.EnsureWithinLimits(query.Filter);
    }

    [Fact]
    public void Every_word_must_match_one_of_the_labels_fields()
    {
        var query = Customers(new RowLabel(["first_name", "last_name"])).Query("ada  love");

        var words = query.Filter.ShouldBeOfType<AlvoAnd>().Filters;
        words.Count.ShouldBe(2);
        words.ShouldAllBe(word => word is AlvoOr && ((AlvoOr)word).Filters.Count == 2);
    }

    [Fact]
    public void An_empty_search_lists_the_first_rows_by_the_required_label()
    {
        var query = Customers(new RowLabel(["name"])).Query("  ");

        query.Filter.ShouldBeNull();
        query.Sort.ShouldHaveSingleItem().ShouldBe(new AlvoSort("name", Descending: false));
    }

    [Fact]
    public void A_nullable_label_is_not_sorted_by()
        => Customers(new RowLabel(["nickname"])).Query(null).Sort.ShouldBeEmpty();

    [Fact]
    public void A_search_matches_at_most_four_words()
        => Customers(new RowLabel(["name"])).Query("a b c d e f").Filter
            .ShouldBeOfType<AlvoAnd>().Filters.Count.ShouldBe(RefPicker.MaxWords);

    [Fact]
    public void A_row_reads_as_its_label_else_its_short_id()
    {
        var page = new AlvoPage
        {
            Items =
            [
                new AlvoRecord(new Dictionary<string, object?> { ["id"] = _ada, ["name"] = "Ada Lovelace" }),
                new AlvoRecord(new Dictionary<string, object?> { ["id"] = _grace.ToString(), ["name"] = null }),
            ],
        };

        Customers(new RowLabel(["name"])).OptionsOf(page)
            .ShouldBe([new RefOption(_ada, "Ada Lovelace"), new RefOption(_grace, RefLabels.ShortId(_grace))]);
    }

    [Fact]
    public void A_target_with_no_label_cannot_be_searched_only_pasted_into()
    {
        var picker = Customers(null);

        picker.Searchable.ShouldBeFalse();
        RefPicker.Pasted($" {_ada} ").ShouldBe(_ada);
        RefPicker.Pasted("Ada").ShouldBeNull();
    }

    [Fact]
    public void The_arrow_keys_wrap_at_either_end()
    {
        var picker = Customers(new RowLabel(["name"]));
        picker.Show([new RefOption(_ada, "Ada"), new RefOption(_grace, "Grace")]);

        picker.Move(-1);
        picker.ActiveOption!.Label.ShouldBe("Grace", "up from nothing lands on the last row");
        picker.Move(1);
        picker.ActiveOption!.Label.ShouldBe("Ada");
    }

    [Fact]
    public void Settling_puts_the_chosen_label_back_and_closes_the_list()
    {
        var picker = Customers(new RowLabel(["name"]));
        picker.Show([new RefOption(_ada, "Ada")]);
        picker.Term = "gra";
        picker.Chosen = "Ada Lovelace";

        picker.Settle(_ada);

        picker.Term.ShouldBe("Ada Lovelace");
        picker.Open.ShouldBeFalse();
    }

    private static RefPicker Customers(RowLabel? label) => new(
        new FieldSchema { Name = "customer_id", Type = FieldType.Ref, Reference = new RefSchema("customers", OnDelete.Restrict) },
        new EntitySchema
        {
            Name = "customers",
            Fields =
            [
                new() { Name = "name", Type = FieldType.String, Required = true },
                new() { Name = "nickname", Type = FieldType.String },
                new() { Name = "first_name", Type = FieldType.String },
                new() { Name = "last_name", Type = FieldType.String },
            ],
        },
        label);
}
