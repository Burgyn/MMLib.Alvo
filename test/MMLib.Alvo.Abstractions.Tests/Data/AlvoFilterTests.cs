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
        Should.NotThrow(() => AlvoFilter.EnsureWithinLimits(Nest(AlvoFilter.MaxDepth)));
    }

    [Fact]
    public void A_tree_one_level_past_the_depth_cap_is_rejected()
    {
        Should.Throw<ArgumentException>(() => AlvoFilter.EnsureWithinLimits(Nest(AlvoFilter.MaxDepth + 1)));
    }

    [Fact]
    public void No_filter_at_all_is_within_the_cap()
    {
        Should.NotThrow(() => AlvoFilter.EnsureWithinLimits(null));
    }

    /// <summary>
    /// The whole reason the cap exists: a tree far past any recursive walker's stack budget must come
    /// back as a rejection, not as a <c>StackOverflowException</c> that no <c>catch</c> can contain.
    /// </summary>
    [Fact]
    public void A_pathologically_deep_tree_is_rejected_rather_than_exhausting_the_stack()
    {
        Should.Throw<ArgumentException>(() => AlvoFilter.EnsureWithinLimits(Nest(50_000)));
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

        Should.NotThrow(() => AlvoFilter.EnsureWithinLimits(wide));
    }

    /// <summary>
    /// Breadth has its own cap, because the depth cap does not see it at all: 900 <c>AND</c> terms answered
    /// on both engines and <b>1000 threw a raw <c>SqliteException</c></b> while PostgreSQL answered — the
    /// engine divergence the per-value guards each closed once and this channel escaped.
    /// </summary>
    [Fact]
    public void A_tree_exactly_at_the_term_cap_is_accepted_and_one_term_past_it_is_rejected()
    {
        Should.NotThrow(() => AlvoFilter.EnsureWithinLimits(Wide(AlvoFilter.MaxTerms)));
        Should.Throw<ArgumentException>(() => AlvoFilter.EnsureWithinLimits(Wide(AlvoFilter.MaxTerms + 1)));
    }

    /// <summary>
    /// The connective counts too: a tree of exactly <see cref="AlvoFilter.MaxTerms"/> comparisons under one
    /// <see cref="AlvoAnd"/> is <see cref="AlvoFilter.MaxTerms"/><c> + 1</c> nodes and must be refused, or the
    /// cap is really "the cap plus however many connectives you nest".
    /// </summary>
    [Fact]
    public void The_term_count_includes_the_connectives()
    {
        var atCap = new AlvoAnd([.. Enumerable.Range(0, AlvoFilter.MaxTerms).Select(i => Comparison($"f{i}"))]);

        Should.Throw<ArgumentException>(() => AlvoFilter.EnsureWithinLimits(atCap));
    }

    /// <summary>
    /// Every <c>in</c> candidate becomes its own bind parameter, so a list is a limit on the statement rather
    /// than on the tree. Measured on SQLite: 32 000 candidates took 4.8 s to compose before answering and
    /// 40 000 threw <c>too many SQL variables</c>, where PostgreSQL answered 40 000 in 0.27 s.
    /// </summary>
    [Fact]
    public void An_in_list_exactly_at_the_candidate_cap_is_accepted_and_one_past_it_is_rejected()
    {
        Should.NotThrow(() => AlvoFilter.EnsureWithinLimits(In(AlvoFilter.MaxInCandidates)));
        Should.Throw<ArgumentException>(() => AlvoFilter.EnsureWithinLimits(In(AlvoFilter.MaxInCandidates + 1)));
    }

    /// <summary>
    /// A candidate list is caller-supplied and may be lazily generated, so the guard that rejects an
    /// over-long one must not enumerate it to the end first — an infinite sequence would hang inside the
    /// check that exists to refuse it.
    /// </summary>
    [Fact]
    public void An_endless_in_list_is_rejected_rather_than_enumerated()
    {
        var endless = new AlvoComparison("title", AlvoFilterOperator.In, Forever());

        Should.Throw<ArgumentException>(() => AlvoFilter.EnsureWithinLimits(endless));
    }

    /// <summary>A bare string operand is one value, never a candidate per character.</summary>
    [Fact]
    public void A_string_in_operand_does_not_count_as_one_candidate_per_character()
    {
        var text = new string('x', AlvoFilter.MaxInCandidates + 1);

        Should.NotThrow(() => AlvoFilter.EnsureWithinLimits(
            new AlvoComparison("title", AlvoFilterOperator.In, text)));
    }

    private static IEnumerable<string> Forever()
    {
        while (true)
        {
            yield return "x";
        }
    }

    private static AlvoComparison In(int candidates) =>
        new("title", AlvoFilterOperator.In, Enumerable.Range(0, candidates).Select(i => $"x{i}").ToList());

    /// <summary>A conjunction of <paramref name="terms"/><c> - 1</c> comparisons, so the whole tree is exactly <paramref name="terms"/> nodes.</summary>
    private static AlvoAnd Wide(int terms) =>
        new([.. Enumerable.Range(0, terms - 1).Select(index => Comparison($"field_{index}"))]);

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

    /// <summary>
    /// A malformed tree must come back as the same fail-closed rejection an over-deep one does, not as
    /// a <see cref="NullReferenceException"/>. These are the two walks every backend is required to
    /// call before touching a row, so an NRE out of one is a far worse signal than a rejection — and
    /// <see cref="AlvoAnd"/>/<see cref="AlvoOr"/>/<see cref="AlvoNot"/> are positional records with no
    /// null guard of their own, so nothing else catches it.
    /// </summary>
    /// <param name="malformed">A tree carrying a <see langword="null"/> where a child belongs.</param>
    [Theory]
    [MemberData(nameof(MalformedTrees))]
    public void A_null_child_is_rejected_rather_than_dereferenced(AlvoFilter malformed)
    {
        Should.Throw<ArgumentException>(() => AlvoFilter.EnsureWithinLimits(malformed));
        Should.Throw<ArgumentException>(() => AlvoFilter.ReferencedFields(malformed).ToList());
    }

    public static TheoryData<AlvoFilter> MalformedTrees() =>
    [
        new AlvoNot(null!),
        new AlvoAnd(null!),
        new AlvoOr(null!),
        new AlvoAnd([Comparison("a"), null!]),
        new AlvoOr([null!]),
        new AlvoNot(new AlvoAnd([Comparison("a"), new AlvoNot(null!)])),
    ];

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
