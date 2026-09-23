using MMLib.Alvo.Admin.Internal;
using MMLib.Alvo.Data;
using MMLib.Alvo.Schema;

namespace MMLib.Alvo.Admin.Tests.Internal;

/// <summary>
/// The grid's quick search and header sort, as the query the data port is sent.
/// </summary>
public class GridQueryTests
{
    private static readonly EntitySchema _orders = new()
    {
        Name = "work_orders",
        Audit = true,
        Fields =
        [
            new FieldSchema { Name = "id", Type = FieldType.Uuid },
            new FieldSchema { Name = "created_by", Type = FieldType.String },
            new FieldSchema { Name = "reference", Type = FieldType.String, Required = true },
            new FieldSchema { Name = "title", Type = FieldType.String },
            new FieldSchema { Name = "description", Type = FieldType.Text },
            new FieldSchema { Name = "access_code", Type = FieldType.String },
            new FieldSchema { Name = "id_document", Type = FieldType.String },
            new FieldSchema { Name = "priority", Type = FieldType.Integer },
            new FieldSchema
            {
                Name = "customer_id",
                Type = FieldType.Ref,
                Reference = new RefSchema("customers", OnDelete.Restrict),
            },
        ],
    };

    private static readonly FieldMasks _masks = new(
        new HashSet<string>(["access_code"], StringComparer.Ordinal),
        new HashSet<string>(["id_document"], StringComparer.Ordinal));

    [Fact]
    public void A_search_looks_in_the_string_fields_no_caller_is_denied()
        => GridQuery.Searchable(_orders, _masks).ShouldBe(
            ["reference", "title"],
            "a masked term refuses the whole query, and a managed column is not what anyone types");

    [Fact]
    public void A_term_is_an_ilike_contains_over_every_searchable_field()
    {
        var filter = GridQuery.Search(["reference", "title"], "  0002 ");

        filter.ShouldBeOfType<AlvoOr>().Filters.ShouldBe(
        [
            new AlvoComparison("reference", AlvoFilterOperator.ILike, "%0002%"),
            new AlvoComparison("title", AlvoFilterOperator.ILike, "%0002%"),
        ]);
        AlvoFilter.EnsureWithinLimits(filter);
    }

    [Fact]
    public void A_term_past_the_data_apis_pattern_bound_is_cut_to_it()
    {
        var filter = GridQuery.Search(["reference"], new string('x', 2_000));

        var pattern = (string)filter.ShouldBeOfType<AlvoOr>().Filters.Cast<AlvoComparison>().Single().Value!;
        pattern.Length.ShouldBe(512, "QueryStringParser.MaxPatternLength, wildcards included");
        pattern.ShouldBe($"%{new string('x', 510)}%");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void A_blank_term_is_no_filter(string? term)
        => GridQuery.Search(["reference"], term).ShouldBeNull();

    [Fact]
    public void An_entity_with_nothing_to_search_is_never_filtered()
        => GridQuery.Search([], "anything").ShouldBeNull();

    [Fact]
    public void An_audited_entity_reads_newest_first_until_a_header_is_chosen()
    {
        GridQuery.Sort(_orders, chosen: null).ShouldBe([new AlvoSort("created_at", Descending: true)]);
        GridQuery.Sort(_orders, new GridSort("priority", Descending: false)).ShouldBe([new AlvoSort("priority")]);
    }

    [Fact]
    public void An_entity_that_is_not_audited_has_no_default_order()
        => GridQuery.Sort(_orders with { Audit = false }, chosen: null).ShouldBeEmpty();

    [Fact]
    public void A_header_click_goes_ascending_then_descending_then_back_to_none()
    {
        var first = GridQuery.Next(null, "priority");
        var second = GridQuery.Next(first, "priority");
        var third = GridQuery.Next(second, "priority");

        first.ShouldBe(new GridSort("priority", Descending: false));
        second.ShouldBe(new GridSort("priority", Descending: true));
        third.ShouldBeNull();
    }

    [Fact]
    public void Another_header_starts_its_own_ascending_sort()
        => GridQuery.Next(new GridSort("priority", Descending: true), "title")
            .ShouldBe(new GridSort("title", Descending: false));

    [Theory]
    [InlineData("priority", true)]
    [InlineData("reference", true)]
    [InlineData("customer_id", false)]
    [InlineData("access_code", false)]
    [InlineData("id_document", false)]
    public void A_header_sorts_unless_it_is_a_reference_or_may_be_masked(string field, bool sortable)
        => GridQuery.Sortable(_orders.Fields.Single(candidate => candidate.Name == field), _masks)
            .ShouldBe(sortable);

    [Fact]
    public void A_page_carries_the_search_the_sort_and_the_cursor_together()
    {
        var search = GridQuery.Search(["reference"], "WO");
        var query = GridQuery.Page(_orders, search, new GridSort("priority", true), limit: 25, after: "cursor");

        query.ShouldBe(new AlvoQuery
        {
            Entity = "work_orders",
            Filter = search,
            Sort = query.Sort,
            Limit = 25,
            After = "cursor",
            IncludeTotalCount = true,
        });
        query.Sort.ShouldBe([new AlvoSort("priority", Descending: true)]);
        query.Offset.ShouldBeNull("keyset paging survives a sort, so the grid never falls back to offsets");
    }
}
