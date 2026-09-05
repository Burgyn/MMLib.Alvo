using MMLib.Alvo.Data;

namespace MMLib.Alvo.Abstractions.Tests;

/// <summary>The states <see cref="AlvoReplaceResult"/> refuses to be in.</summary>
/// <remarks>
/// <b>Small, but not pointless.</b> This type crosses the port, so a third-party <see cref="IAlvoData"/>
/// builds one — and the one thing a caller cannot recover if it is wrong is which branch wrote the row,
/// because a created row and a replaced one are the same shape.
/// </remarks>
public sealed class AlvoReplaceResultTests
{
    private static readonly AlvoRecord _row =
        new(new Dictionary<string, object?>(StringComparer.Ordinal) { ["id"] = Guid.NewGuid() });

    /// <summary>There is no result without a row: both branches produce one.</summary>
    [Fact]
    public void A_result_must_carry_a_row() =>
        Should.Throw<ArgumentNullException>(() => new AlvoReplaceResult(null!, Created: true));

    /// <summary>The two shapes are built, so the refusal above is not simply refusing everything.</summary>
    [Fact]
    public void The_two_shapes_are_accepted()
    {
        AlvoReplaceResult.CreatedRow(_row).Created.ShouldBeTrue();
        AlvoReplaceResult.ReplacedRow(_row).Created.ShouldBeFalse();
        AlvoReplaceResult.CreatedRow(_row).Row.ShouldBeSameAs(_row);
    }

    /// <summary>And a <c>with</c> expression cannot rebuild either of them as the other.</summary>
    /// <remarks>
    /// <b>A <c>with</c> does not run the constructor</b>, so an <c>init</c> setter would let
    /// <c>ReplacedRow(row) with { Created = true }</c> report an act of creation that never happened — and
    /// the endpoint would answer <c>201</c> with a <c>Location</c> for a row it did not create. The members
    /// are get-only, so this is a compile-time refusal; the fact asserts the property that makes it one.
    /// </remarks>
    [Fact]
    public void The_members_are_not_settable_so_a_with_expression_cannot_relabel_the_branch()
    {
        foreach (var name in new[] { "Row", "Created" })
        {
            typeof(AlvoReplaceResult).GetProperty(name)!.SetMethod.ShouldBeNull(
                $"'{name}' has a setter, so a 'with' expression can report a branch that never ran");
        }
    }
}
