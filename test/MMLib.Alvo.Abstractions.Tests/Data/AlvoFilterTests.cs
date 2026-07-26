using MMLib.Alvo.Data;

namespace MMLib.Alvo.Abstractions.Tests.Data;

/// <summary>
/// The two walks the closed <see cref="AlvoFilter"/> hierarchy owns on behalf of every
/// <see cref="IAlvoData"/> implementation: the depth cap that keeps a caller-built tree from
/// exhausting a recursive evaluator's stack, and the field enumeration each implementation validates
/// against the schema and the caller's field mask. Both are asserted at and just past the boundary,
/// because a cap that is off by one is a cap nobody can reason about.
/// </summary>
public class AlvoFilterTests
{
    [Fact]
    public void A_tree_exactly_at_the_depth_cap_is_accepted()
    {
        Should.NotThrow(() => AlvoFilter.EnsureWithinDepthLimit(Nest(AlvoFilter.MaxDepth)));
    }

    [Fact]
    public void A_tree_one_level_past_the_depth_cap_is_rejected()
    {
        Should.Throw<ArgumentException>(() => AlvoFilter.EnsureWithinDepthLimit(Nest(AlvoFilter.MaxDepth + 1)));
    }

    [Fact]
    public void No_filter_at_all_is_within_the_cap()
    {
        Should.NotThrow(() => AlvoFilter.EnsureWithinDepthLimit(null));
    }

    /// <summary>
    /// The whole reason the cap exists: a tree far past any recursive walker's stack budget must come
    /// back as a rejection, not as a <c>StackOverflowException</c> that no <c>catch</c> can contain.
    /// </summary>
    [Fact]
    public void A_pathologically_deep_tree_is_rejected_rather_than_exhausting_the_stack()
    {
        Should.Throw<ArgumentException>(() => AlvoFilter.EnsureWithinDepthLimit(Nest(50_000)));
    }

    /// <summary>
    /// Depth is the longest root-to-leaf path, not the node count: a single <see cref="AlvoAnd"/> over
    /// a hundred comparisons is two levels deep and must stay accepted, or the cap would silently
    /// become a limit on how many terms a legitimate query string may carry.
    /// </summary>
    [Fact]
    public void Breadth_does_not_count_towards_the_depth_cap()
    {
        var wide = new AlvoAnd([.. Enumerable.Range(0, 100).Select(index => Comparison($"field_{index}"))]);

        Should.NotThrow(() => AlvoFilter.EnsureWithinDepthLimit(wide));
    }

    [Fact]
    public void Referenced_fields_enumerates_every_comparison_in_a_nested_tree()
    {
        var tree = new AlvoNot(new AlvoOr([Comparison("a"), new AlvoAnd([Comparison("b"), Comparison("c")])]));

        AlvoFilter.ReferencedFields(tree).ShouldBe(["a", "b", "c"], ignoreOrder: true);
    }

    [Fact]
    public void Referenced_fields_of_no_filter_is_empty()
    {
        AlvoFilter.ReferencedFields(null).ShouldBeEmpty();
    }

    private static AlvoComparison Comparison(string field) => new(field, AlvoFilterOperator.Eq, "x");

    private static AlvoFilter Nest(int depth)
    {
        AlvoFilter node = Comparison("title");
        for (var level = 1; level < depth; level++)
        {
            node = new AlvoNot(node);
        }

        return node;
    }
}
