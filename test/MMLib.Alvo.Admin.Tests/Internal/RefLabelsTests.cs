using MMLib.Alvo.Admin.Internal;
using MMLib.Alvo.Data;

namespace MMLib.Alvo.Admin.Tests.Internal;

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
        var query = RefLabels.Query("bikes", "model", [_bike, _other]);

        query.Entity.ShouldBe("bikes");
        var filter = query.Filter.ShouldBeOfType<AlvoComparison>();
        filter.Field.ShouldBe("id");
        filter.Operator.ShouldBe(AlvoFilterOperator.In);
        filter.Value.ShouldBeAssignableTo<IEnumerable<Guid>>()!.ShouldBe([_bike, _other]);
        query.Select.ShouldBe(["id", "model"]);
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
}
