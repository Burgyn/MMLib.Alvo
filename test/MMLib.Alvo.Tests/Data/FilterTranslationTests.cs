using MMLib.Alvo.Data;
using MMLib.Alvo.Data.Internal;

using System.Linq.Expressions;

namespace MMLib.Alvo.Tests.Data;

/// <summary>
/// The query column of spec 2026-10-14 §4.3 as data (HX-05 acceptance (1)): each lambda's <c>AlvoFilter</c>, with values
/// placed as typed values for the driver to bind; every refused construct throws naming the entity and a fix.
/// </summary>
public sealed class FilterTranslationTests
{
    private static readonly OrderStatus[] _open = [OrderStatus.Draft, OrderStatus.InReview];
    private static readonly string[] _noNotes = [];
    private static readonly DateTimeOffset _cutoff = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateOnly _day = new(2026, 11, 2);
    private static readonly int[] _ranks = [1, 2];
    private static readonly OrderStatus _approved = OrderStatus.Approved;
    private static readonly HashSet<string> _tags = ["a", "b"];
    private static readonly HashSet<string> _looseTags = new(["a"], StringComparer.OrdinalIgnoreCase);
    private static readonly string?[] _maybeCodes = ["a", null];
    private static readonly SortedSet<string> _sorted = new(["a"], StringComparer.OrdinalIgnoreCase);

    public static TheoryData<string, Expression<Func<Order, bool>>, string> Translated()
    {
        var note = "it's";
        var wildcard = "50%_";
        List<Guid> ids = [Guid.Parse("11111111-1111-1111-1111-111111111111"), Guid.Parse("22222222-2222-2222-2222-222222222222")];
        var lazy = Lazy();

        return new()
        {
            { "equality", x => x.Note == "a", "note eq 'a'" },
            { "[AlvoField] name", x => x.Reference == "R-1", "ref_no eq 'R-1'" },
            { "greater", x => x.Quantity > 3, "quantity gt 3" },
            { "value on the left", x => 3 < x.Quantity, "quantity gt 3" },
            { "less or equal", x => x.Total <= 10.5m, "total lte 10.5" },
            { "a flat and", x => x.Quantity != 2 && x.Total >= 1m && x.Rank < 9, "and(quantity neq 2, total gte 1, rank lt 9)" },
            { "bool field and null", x => x.IsPaid || x.Code == null, "or(is_paid eq true, code is null)" },
            { "negated bool field", x => !x.IsPaid, "not(is_paid eq true)" },
            { "not null and HasValue", x => x.Code != null && x.DueAt.HasValue, "and(not(code is null), not(due_at is null))" },
            { "enum member", x => x.Status == OrderStatus.Approved, "status eq 'ok'" },
            { "nullable enum", x => x.Previous == OrderStatus.Draft, "previous eq 'draft'" },
            { "constant enum list", x => _open.Contains(x.Status), "status in ['draft', 'in_review']" },
            { "captured id list", x => ids.Contains(x.Id), "id in [11111111-1111-1111-1111-111111111111, 22222222-2222-2222-2222-222222222222]" },
            { "inline text list", x => new[] { "a", "b" }.Contains(x.Note), "note in ['a', 'b']" },
            { "empty list", x => _noNotes.Contains(x.Note), "or()" },
            { "instant", x => x.DueAt < _cutoff, "due_at lt 2026-01-01T00:00:00.0000000+00:00" },
            { "captured text", x => x.Note == note, "note eq 'it's'" },
            { "day", x => x.ShipOn == _day, "ship_on eq 2026-11-02" },
            { "nested groups", x => x.Note == "a" && (x.Quantity > 1 || x.IsPaid), "and(note eq 'a', or(quantity gt 1, is_paid eq true))" },
            { "flipped order", x => 10.5m >= x.Total, "total lte 10.5" },
            { "null on the left", x => null == x.Code, "code is null" },
            { "enum on the left", x => OrderStatus.InReview != x.Status, "status neq 'in_review'" },
            { "enum number", x => (int)x.Status == 1, "status eq 'in_review'" },
            { "list of enum numbers", x => new[] { 0, 1 }.Contains((int)x.Status), "status in ['draft', 'in_review']" },
            { "enum constant as a number", x => x.Quantity == (int)_approved, "quantity eq 2" },
            { "enum constants in a number list", x => new long[] { (int)_approved, 0 }.Contains(x.Quantity), "quantity in [2, 0]" },
            { "value of a nullable", x => x.Rank!.Value > 2, "rank gt 2" },
            { "set with the default comparer", x => _tags.Contains(x.Note), "note in ['a', 'b']" },
            { "lazy sequence", x => lazy.Contains(x.Note), "note in ['x!', 'y!']" },
            { "starts with", x => x.Note.StartsWith("ab"), "note startswith 'ab'" },
            { "ordinal ends with", x => x.Note.EndsWith("ab", StringComparison.Ordinal), "note endswith 'ab'" },
            { "contains a captured wildcard", x => x.Note.Contains(wildcard), "note contains '50%_'" },
            { "text test on a nullable text", x => x.Code!.StartsWith("C-"), "code startswith 'C-'" },
            { "or chain", x => x.Quantity == 1 || x.Quantity == 2 || x.IsPaid, "or(quantity eq 1, quantity eq 2, is_paid eq true)" },
        };
    }

    public static TheoryData<string, Expression<Func<Order, bool>>, string> Refused()
    {
        var many = Enumerable.Range(0, 1001).Select(number => (long)number).ToArray();
        var flag = true;
        var endless = Endless(new Pulls());
        int? none = null;
        string[]? missing = null;
        string? missingText = null;

        return new()
        {
            { "culture-folding comparison", x => x.Note.StartsWith("ab", StringComparison.OrdinalIgnoreCase), "translates only in its one-argument form or with StringComparison.Ordinal" },
            { "char overload", x => x.Note.StartsWith('a'), "translates only in its one-argument form or with StringComparison.Ordinal" },
            { "text against a field", x => x.Note.EndsWith(x.Code!), "it compares a field with a field" },
            { "comparison read from the row", x => x.Note.StartsWith("ab", x.Status == OrderStatus.Draft ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase), "translates only in its one-argument form or with StringComparison.Ordinal" },
            { "value tested against a field", x => "abc-prefix".StartsWith(x.Code!), "the text it tests must be a field" },
            { "null text", x => x.Note.Contains(missingText!), "the text is null" },
            { "arithmetic", x => x.Total * 2m > 10m, "arithmetic has no filter equivalent" },
            { "two fields", x => x.Quantity > x.Rank, "it compares two fields" },
            { "two enum fields", x => x.Status == x.Previous, "it compares two fields" },
            { "method on a field", x => x.Note.Trim() == "a", "method 'Trim' has no filter equivalent" },
            { "clock", x => DateTimeOffset.UtcNow > x.DueAt, "a query has no clock" },
            { "clock in a list", x => new[] { DateTimeOffset.UtcNow }.Contains(x.DueAt!.Value), "a query has no clock" },
            { "relational enum", x => x.Status > OrderStatus.Draft, "an enum field compares with == and != only" },
            { "relational enum on the left", x => OrderStatus.Draft <= x.Previous, "an enum field compares with == and != only" },
            { "too many candidates", x => many.Contains(x.Quantity), "it lists 1001 candidates" },
            { "endless candidates", x => endless.Contains(x.Quantity), "it lists more than 1000 candidates" },
            { "unmapped property", x => x.Summary == "a", "Order.Summary maps to no field" },
            { "object comparison", x => (object)x.Status == (object)OrderStatus.Draft, "compares references in C#" },
            { "arithmetic on an enum", x => (int)x.Status + 1 == 2, "arithmetic has no filter equivalent" },
            { "undeclared enum number", x => (int)x.Status == 99, "OrderStatus has no member 99" },
            { "enum number past its underlying type", x => (long)x.Status == 5_000_000_000L, "outside the range of OrderStatus's underlying Int32" },
            { "set with its own comparer", x => _looseTags.Contains(x.Note), "carries its own comparer" },
            { "collection with its own Contains", x => ((IEnumerable<string>)_sorted).Contains(x.Note), "a SortedSet may compare with its own equality" },
            { "null list", x => missing!.Contains(x.Note), "the list is null" },
            { "null candidate", x => _maybeCodes.Contains(x.Code), "a candidate is null" },
            { "list read from the row", x => x.Note.Split(',', StringSplitOptions.None).Contains("a"), "Contains translates only as a list of values containing a field" },
            { "relational null", x => x.Rank < none, "only == and != can test for null" },
            { "captured condition", x => flag && x.IsPaid, "it reads no field" },
            { "constant true", x => true, "it reads no field" },
            { "conditional", x => (x.IsPaid ? 1 : 0) == 1, "a conditional (?:) has no filter equivalent" },
        };
    }

    [Theory]
    [MemberData(nameof(Translated))]
    public void A_predicate_translates_to_its_filter(string row, Expression<Func<Order, bool>> where, string filter) =>
        FilterText.Of(FilterTranslator.Translate(where, "orders")).ShouldBe(filter, row);

    [Theory]
    [MemberData(nameof(Refused))]
    public void A_construct_with_no_filter_equivalent_is_refused_naming_the_entity_and_a_fix(string row, Expression<Func<Order, bool>> where, string reason)
    {
        var refusal = Should.Throw<AlvoTranslationException>(() => FilterTranslator.Translate(where, "orders"));

        refusal.Message.ShouldContain("in a typed query on 'orders': ", customMessage: row);
        refusal.Message.ShouldContain(reason, customMessage: row);
        refusal.FixSuggestion.ShouldNotBeNullOrWhiteSpace(row);
    }

    private static IEnumerable<long> Endless(Pulls pulls)
    {
        while (true)
        {
            yield return ++pulls.Count;
        }
    }

    private static IEnumerable<string> Lazy()
    {
        yield return "x!";
        yield return "y!";
    }

    [Fact]
    public void An_endless_list_is_refused_after_pulling_no_more_than_one_past_the_cap()
    {
        var pulls = new Pulls();
        var endless = Endless(pulls);

        Should.Throw<AlvoTranslationException>(() => FilterTranslator.Translate<Order>(x => endless.Contains(x.Quantity), "orders"));

        pulls.Count.ShouldBeLessThanOrEqualTo(AlvoFilter.MaxInCandidates + 1);
    }

    [Fact]
    public void A_predicate_too_deep_to_walk_is_refused_without_reading_it_through()
    {
        var x = Expression.Parameter(typeof(Order), "x");
        var quantity = Expression.Property(x, nameof(Order.Quantity));
        var body = Enumerable.Range(0, 50_000)
            .Select(number => (Expression)Expression.Equal(quantity, Expression.Constant((long)number)))
            .Aggregate(Expression.OrElse);

        var refusal = Should.Throw<AlvoTranslationException>(() => FilterTranslator.Translate(Expression.Lambda<Func<Order, bool>>(body, x), "orders"));

        refusal.Message.ShouldContain("in a typed query on 'orders'");
        refusal.Message.ShouldContain("more than a filter of");
        refusal.Message.Length.ShouldBeLessThan(1000);
        refusal.FixSuggestion.ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public void A_predicate_wider_than_a_filter_can_hold_is_refused_before_it_is_walked()
    {
        var x = Expression.Parameter(typeof(Order), "x");
        var quantity = Expression.Property(x, nameof(Order.Quantity));
        var terms = Enumerable.Range(0, 4096).Select(number => (Expression)Expression.Equal(quantity, Expression.Constant((long)number))).ToList();
        while (terms.Count > 1)
        {
            terms = [.. terms.Chunk(2).Select(pair => pair.Length == 2 ? Expression.OrElse(pair[0], pair[1]) : pair[0])];
        }

        Should.Throw<AlvoTranslationException>(() => FilterTranslator.Translate(Expression.Lambda<Func<Order, bool>>(terms[0], x), "orders"))
            .Message.ShouldContain("more than a filter of");
    }

    [Fact]
    public void An_or_chain_as_long_as_a_filter_may_hold_still_translates()
    {
        var x = Expression.Parameter(typeof(Order), "x");
        var quantity = Expression.Property(x, nameof(Order.Quantity));
        var body = Enumerable.Range(0, AlvoFilter.MaxTerms - 1)
            .Select(number => (Expression)Expression.Equal(quantity, Expression.Constant((long)number)))
            .Aggregate(Expression.OrElse);

        FilterTranslator.Translate(Expression.Lambda<Func<Order, bool>>(body, x), "orders").ShouldBeOfType<AlvoOr>().Filters.Count.ShouldBe(AlvoFilter.MaxTerms - 1);
    }

    [Fact]
    public void A_bare_record_is_not_a_row_whose_keys_a_filter_reads() =>
        Should.Throw<AlvoTranslationException>(() => FilterTranslator.Translate<AlvoRecord>(x => (string?)x["legacy_code"] == "L-9", "orders"))
            .Message.ShouldContain("in a typed query on 'orders'");

    [Fact]
    public void A_captured_value_is_placed_as_a_typed_value_never_as_text()
    {
        var payload = "x'; DROP TABLE orders; --";

        var comparison = FilterTranslator.Translate<Order>(x => x.Note == payload, "orders").ShouldBeOfType<AlvoComparison>();

        comparison.Value.ShouldBeSameAs(payload);
    }

    [Fact]
    public void A_captured_value_is_read_again_on_every_call()
    {
        var note = "first";
        Expression<Func<Order, bool>> where = x => x.Note == note;

        var first = FilterTranslator.Translate(where, "orders");
        note = "second";
        var second = FilterTranslator.Translate(where, "orders");

        (FilterText.Of(first), FilterText.Of(second)).ShouldBe(("note eq 'first'", "note eq 'second'"));
    }

    [Fact]
    public void An_integral_value_is_widened_to_the_long_an_integer_field_holds()
    {
        FilterTranslator.Translate<Order>(x => x.Rank < 9, "orders").ShouldBeOfType<AlvoComparison>().Value.ShouldBeOfType<long>();
        var membership = FilterTranslator.Translate<Order>(x => _ranks.Contains(x.Rank!.Value), "orders").ShouldBeOfType<AlvoComparison>();
        ((IEnumerable<object?>)membership.Value!).ShouldAllBe(candidate => candidate is long);
    }

    [Fact]
    public void A_wrapper_filters_any_field_through_its_record() =>
        FilterText.Of(FilterTranslator.Translate<OrderView>(x => (string?)x.Record["legacy_code"] == "L-9", "orders")).ShouldBe("legacy_code eq 'L-9'");

    [Fact]
    public void A_wrapper_filters_its_mapped_property() =>
        FilterText.Of(FilterTranslator.Translate<OrderView>(x => x.Note == "a", "orders")).ShouldBe("note eq 'a'");

    [Fact]
    public void A_record_key_that_is_not_a_literal_is_refused()
    {
        var key = "legacy_code";

        Should.Throw<AlvoTranslationException>(() => FilterTranslator.Translate<OrderView>(x => (string?)x.Record[key] == "a", "orders"))
            .Message.ShouldContain("a record's key must be a literal string");
    }

    [Fact]
    public void A_record_key_that_is_not_a_field_name_is_refused() =>
        Should.Throw<AlvoTranslationException>(() => FilterTranslator.Translate<OrderView>(x => (string?)x.Record["a == 'x' or 1"] == "a", "orders"))
            .Message.ShouldContain("is not a field name");

    [Fact]
    public void A_value_no_field_type_holds_is_refused() =>
        Should.Throw<AlvoTranslationException>(() => FilterTranslator.Translate<TicketWithScore>(x => x.Score > 1.5, "tickets"))
            .Message.ShouldContain("a Double value matches no field type");
}

/// <summary>How many items a test's endless sequence has handed out.</summary>
internal sealed class Pulls
{
    public long Count { get; set; }
}
