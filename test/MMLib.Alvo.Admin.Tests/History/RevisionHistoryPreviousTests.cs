using MMLib.Alvo.Admin.Components.History;
using MMLib.Alvo.Management;

namespace MMLib.Alvo.Admin.Tests.History;

/// <summary>Which revision a revision's change is shown against (inventory defect #4).</summary>
public sealed class RevisionHistoryPreviousTests
{
    private static readonly DateTimeOffset _at = new(2026, 9, 24, 12, 0, 0, TimeSpan.Zero);

    private static readonly ManagementRevision[] _newestFirst =
    [
        new(5, _at, "a@x", null, null),
        new(4, _at, "a@x", null, 2),
        new(2, _at, "a@x", null, null),
        new(1, _at, null, null, null),
    ];

    [Theory]
    [InlineData(5, 4)]
    [InlineData(4, 2)]
    [InlineData(2, 1)]
    public void It_is_the_nearest_earlier_revision_listed_whatever_the_gap(int revision, int previous)
        => RevisionHistory.Previous(_newestFirst, revision).ShouldBe(previous);

    [Fact]
    public void The_first_revision_has_none()
        => RevisionHistory.Previous(_newestFirst, 1).ShouldBeNull();

    [Fact]
    public void The_order_of_the_list_does_not_matter()
        => RevisionHistory.Previous([.. _newestFirst.Reverse()], 5).ShouldBe(4);
}
