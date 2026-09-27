using MMLib.Alvo.Admin.Components.Data;
using MMLib.Alvo.Admin.Internal;
using MMLib.Alvo.Data;
using MMLib.Alvo.Schema;

namespace MMLib.Alvo.Admin.Tests.Data;

/// <summary>
/// The version a record's save and delete carry, and the conflict it turns into when somebody wrote first
/// (docs/todo-admin.md §8d item 25).
/// </summary>
public class RecordVersionTests
{
    private static readonly DateTimeOffset _read = new(2026, 9, 27, 10, 15, 30, 123, TimeSpan.Zero);

    private static readonly EntitySchema _audited = new()
    {
        Name = "work_orders",
        Audit = true,
        Fields = [new FieldSchema { Name = "id", Type = FieldType.Uuid }, new FieldSchema { Name = "updated_at", Type = FieldType.DateTime }],
    };

    private static readonly EntitySchema _unaudited = new()
    {
        Name = "customers",
        Fields = [new FieldSchema { Name = "id", Type = FieldType.Uuid }, new FieldSchema { Name = "name", Type = FieldType.String }],
    };

    [Fact]
    public void An_audited_record_carries_the_updated_at_it_was_read_with()
        => RecordVersion.Of(_audited, new Dictionary<string, object?> { ["updated_at"] = _read })
            .ShouldBe(new AlvoPrecondition(_read));

    [Fact]
    public void An_unaudited_record_carries_none_because_the_port_would_refuse_it()
        => RecordVersion.Of(_unaudited, new Dictionary<string, object?> { ["updated_at"] = _read }).ShouldBeNull();

    [Theory]
    [InlineData(null)]
    [InlineData("2026-09-27T10:15:30Z")]
    public void A_version_that_was_not_read_as_an_instant_is_none(object? value)
        => RecordVersion.Of(_audited, new Dictionary<string, object?> { ["updated_at"] = value }).ShouldBeNull();

    [Fact]
    public void Last_write_wins_exactly_where_no_version_is_sent()
    {
        RecordVersion.LastWriteWins(_unaudited, new Dictionary<string, object?>()).ShouldBeTrue();
        RecordVersion.LastWriteWins(_audited, new Dictionary<string, object?> { ["updated_at"] = _read }).ShouldBeFalse();
        RecordVersion.LastWriteWins(_audited, new Dictionary<string, object?>())
            .ShouldBeTrue("an audited row read without its version is written without one, and the screen says so");
    }

    [Fact]
    public void An_audited_record_read_without_its_version_does_not_claim_the_entity_is_unaudited()
    {
        RecordVersion.Caveat(_unaudited, new Dictionary<string, object?>()).ShouldBe(RecordVersion.LastWriteWinsSentence);
        RecordVersion.Caveat(_audited, new Dictionary<string, object?>())
            .ShouldBe(RecordVersion.UnreadableVersionSentence);
        RecordVersion.UnreadableVersionSentence.ShouldNotContain("audited");
        RecordVersion.Caveat(_audited, new Dictionary<string, object?> { ["updated_at"] = _read }).ShouldBeNull();
    }

    [Fact]
    public void A_stale_version_and_a_vanished_record_are_both_conflicts_Reload_answers()
    {
        RecordVersion.IsConflict(Problem(new AlvoPreconditionFailedException())).ShouldBeTrue();
        RecordVersion.IsConflict(Problem(new AlvoRecordNotFoundException())).ShouldBeTrue();
        RecordVersion.IsConflict(Problem(new AlvoAuthorizationException("no"))).ShouldBeFalse();
        RecordVersion.IsConflict(null).ShouldBeFalse();
    }

    [Fact]
    public void A_conflict_says_changed_since_you_opened_it_and_that_Reload_discards_the_edit()
    {
        var problem = AdminProblem.From(new AlvoPreconditionFailedException(), ProblemSite.RecordWrite)!;

        RecordVersion.ConflictTitle.ShouldBe("This record changed since you opened it");
        problem.Fix.ShouldNotBeNull().ShouldContain("Reload");
        problem.Fix.ShouldContain("since you opened it");
    }

    private static AdminProblem? Problem(Exception exception) => AdminProblem.From(exception, ProblemSite.RecordWrite);
}
