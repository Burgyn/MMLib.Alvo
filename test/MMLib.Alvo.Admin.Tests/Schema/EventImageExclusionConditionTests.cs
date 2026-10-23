using MMLib.Alvo.Admin.Components.Schema;

namespace MMLib.Alvo.Admin.Tests.Schema;

/// <summary>
/// The guided condition never offers, nor reads back as rows, a read of a field the entity leaves out of its event images
/// after the commit — apply refuses it (HX-01, <c>AfterHookCompiler.ExcludedFieldReads</c>); <c>changed</c> stays.
/// </summary>
public class EventImageExclusionConditionTests
{
    private static readonly ConditionField _excluded = new("status", ConditionFieldKind.Text, false, [], ExcludedFromEvents: true);
    private static readonly ConditionField _kept = _excluded with { Name = "code", ExcludedFromEvents = false };
    private static readonly ConditionScope _scope = new([_excluded, _kept], []);

    [Fact]
    public void After_an_update_an_excluded_field_is_offered_only_for_changed()
        => ConditionTable.For("afterUpdate", _excluded).Select(spec => spec.Operator).ShouldBe([ConditionOperator.Changed]);

    [Theory]
    [InlineData("afterCreate")]
    [InlineData("afterDelete")]
    public void After_a_create_or_a_delete_an_excluded_field_is_offered_nothing(string point)
        => ConditionTable.For(point, _excluded).ShouldBeEmpty();

    [Theory]
    [InlineData("beforeCreate")]
    [InlineData("beforeUpdate")]
    [InlineData("afterUpdate")]
    public void A_kept_field_is_offered_what_its_kind_is(string point)
        => ConditionTable.For(point, _kept).ShouldBe(ConditionTable.For(point, _kept.Kind, _kept.Nullable));

    [Fact]
    public void Before_the_commit_an_excluded_field_is_offered_what_its_kind_is()
        => ConditionTable.For("beforeUpdate", _excluded).ShouldBe(ConditionTable.For("beforeUpdate", _excluded.Kind, _excluded.Nullable));

    [Theory]
    [InlineData("new.status == 'done'", "afterUpdate")]
    [InlineData("old.status == 'done'", "afterDelete")]
    public void A_read_of_an_excluded_field_after_the_commit_stays_text(string text, string point)
        => ConditionText.Recognize(text, point, _scope).ShouldBeNull();

    [Theory]
    [InlineData("changed(status)", "afterUpdate")]
    [InlineData("new.status == 'done'", "beforeUpdate")]
    [InlineData("new.code == 'done'", "afterUpdate")]
    public void What_apply_accepts_is_still_read_back_as_rows(string text, string point)
        => ConditionText.Recognize(text, point, _scope).ShouldNotBeNull();

    [Fact]
    public void The_scope_marks_the_fields_the_working_copy_excludes()
    {
        var copy = new WorkingCopy();
        copy.Take(
            """
            {
              "apiVersion": "v1",
              "name": "field-service",
              "entities": {
                "work_orders": {
                  "fields": { "status": { "type": "string" }, "code": { "type": "string" } },
                  "events": { "exclude": ["status"] }
                }
              }
            }
            """,
            revision: 1);

        var scope = ConditionScope.Of(copy, "work_orders");

        scope.Field("status")!.ExcludedFromEvents.ShouldBeTrue();
        scope.Field("code")!.ExcludedFromEvents.ShouldBeFalse();
        copy.ExcludedFromEvents("work_orders").ShouldBe(["status"]);
    }
}
