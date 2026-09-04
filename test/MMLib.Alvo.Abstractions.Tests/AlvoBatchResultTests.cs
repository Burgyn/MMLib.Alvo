using MMLib.Alvo.Data;

namespace MMLib.Alvo.Abstractions.Tests;

/// <summary>
/// The states <see cref="AlvoBatchResult"/> refuses to be in — the ones its own remarks call impossible.
/// </summary>
/// <remarks>
/// <b>Enforced rather than described, because this type crosses the port.</b> A third-party
/// <see cref="IAlvoData"/> builds one of these, and a sentence saying a result carrying both rows and
/// refusals "would describe one that cannot happen" binds nobody. A provider that returned it would make
/// <see cref="AlvoBatchResult.Succeeded"/> answer <see langword="false"/> beside written rows, and every
/// caller branching on it would be wrong the same way.
/// </remarks>
public sealed class AlvoBatchResultTests
{
    private static readonly IReadOnlyList<AlvoRecord> _rows =
        [new AlvoRecord(new Dictionary<string, object?>(StringComparer.Ordinal) { ["id"] = Guid.NewGuid() })];

    private static readonly IReadOnlyList<AlvoRowRefusal> _refusals =
        [new AlvoRowRefusal(0, "forbidden", "Refused.", null)];

    /// <summary>A result may not carry rows and refusals at once.</summary>
    [Fact]
    public void A_result_carrying_rows_and_refusals_is_refused() =>
        Should.Throw<ArgumentException>(() => new AlvoBatchResult(1, _rows, _refusals));

    /// <summary>Nor a non-zero affected count beside refusals.</summary>
    [Fact]
    public void A_refused_result_may_not_report_rows_affected() =>
        Should.Throw<ArgumentException>(() => new AlvoBatchResult(1, [], _refusals));

    /// <summary>Nor a claim to have written nothing while naming no reason.</summary>
    [Fact]
    public void A_result_that_wrote_nothing_must_name_a_reason() =>
        Should.Throw<ArgumentException>(() => new AlvoBatchResult(0, [], []));

    /// <summary>
    /// And a <c>with</c> expression cannot rebuild any of them, which is what makes the check binding.
    /// </summary>
    /// <remarks>
    /// <b>A <c>with</c> does not run the constructor</b>, so an <c>init</c> setter would let
    /// <c>Wrote(rows, 1) with { Refusals = … }</c> produce exactly the state the constructor refuses — a
    /// validated type whose validation covers only the paths nobody was going to take. The members are
    /// get-only, so this is a compile-time refusal; the fact asserts the property that makes it one.
    /// </remarks>
    [Fact]
    public void The_members_are_not_settable_so_a_with_expression_cannot_rebuild_a_refused_state()
    {
        foreach (var name in new[] { "Affected", "Rows", "Refusals" })
        {
            typeof(AlvoBatchResult).GetProperty(name)!.SetMethod.ShouldBeNull(
                $"'{name}' has a setter, so a 'with' expression can rebuild a state the constructor refuses");
        }
    }

    /// <summary>The two valid shapes are built, so the refusals above are not simply refusing everything.</summary>
    [Fact]
    public void The_two_valid_shapes_are_accepted()
    {
        AlvoBatchResult.Wrote(_rows, 1).Succeeded.ShouldBeTrue();
        AlvoBatchResult.Wrote([], 2).Affected.ShouldBe(2, "a delete writes no rows and still affects some");
        AlvoBatchResult.Refused(_refusals).Succeeded.ShouldBeFalse();
    }
}
