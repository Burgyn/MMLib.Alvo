using MMLib.Alvo.Admin.Components.Schema;

namespace MMLib.Alvo.Admin.Tests.Schema;

/// <summary>The strict recognizer: a condition is rows only when it is exactly what the generator writes (spec §7.3, D5).</summary>
public class ConditionTextRecognitionTests
{
    private static readonly ConditionScope _scope = new(
    [
        new("status", ConditionFieldKind.Choice, false, ["received", "ready", "collected", "cancelled"]),
        new("priority", ConditionFieldKind.Choice, false, ["normal", "express"]),
        new("quantity", ConditionFieldKind.Number, false, []),
        new("discount", ConditionFieldKind.Number, true, []),
        new("title", ConditionFieldKind.Text, false, []),
        new("note", ConditionFieldKind.Text, true, []),
        new("notify_customer", ConditionFieldKind.Flag, true, []),
        new("owner_id", ConditionFieldKind.Identity, true, []),
        new("due_on", ConditionFieldKind.Moment, true, []),
    ],
    ["manager", "reception"]);

    [Theory]
    [InlineData("beforeUpdate", "old.status == 'collected' && new.status != 'collected'")]
    [InlineData("beforeCreate", "new.quantity <= 0")]
    [InlineData("beforeDelete", "old.status != 'received' && old.status != 'cancelled'")]
    [InlineData("afterCreate", "new.priority == 'express'")]
    [InlineData("beforeUpdate", "changed(status) && new.status == 'ready'")]
    [InlineData("beforeUpdate", "!has(new.note) || 'reception' in @user.roles")]
    [InlineData("beforeUpdate", "new.title == 'a && b' && new.title != 'it\\'s'")]
    [InlineData("beforeUpdate", "")]
    [InlineData("afterUpdate", "changed(status)")]
    [InlineData("beforeUpdate", "new.notify_customer == true || new.owner_id == @user.id || !('manager' in @user.roles)")]
    [InlineData("beforeUpdate", "new.title == 'line\\nbreak\\ttab\\\\'")]
    [InlineData("beforeUpdate", "new.title == 'a‍b' && new.note == 'می‌خواهم'")]
    [InlineData("beforeUpdate", "!(new.discount == 4.5) && new.discount > 0.01")]
    public void A_canonical_condition_is_read_as_rows_that_write_it_back(string point, string text)
        => ConditionText.Generate(ConditionText.Recognize(text, point, _scope).ShouldNotBeNull()).ShouldBe(text);

    [Fact]
    public void The_rows_read_are_the_ones_written()
    {
        var condition = ConditionText.Recognize("old.status == 'collected' || 'manager' in @user.roles", "beforeUpdate", _scope).ShouldNotBeNull();

        condition.ShouldBe(new GuidedCondition(false,
        [
            new ConditionRow(ConditionOperator.Is, RowImage.Old, "status", ConditionFieldKind.Choice, "collected"),
            new ConditionRow(ConditionOperator.HasRole, RowImage.New, ConditionTable.Writer, ConditionFieldKind.Text, "manager"),
        ]));
    }

    [Theory]
    [InlineData("beforeUpdate", "new.status == \"ready\"", "double quotes are not the generator's")]
    [InlineData("beforeUpdate", "new.status=='ready'", "spacing")]
    [InlineData("beforeUpdate", "new.status == 'ready' && new.quantity > 1 || new.title == 'x'", "a mix of && and ||")]
    [InlineData("beforeUpdate", "new.nope == 'x'", "an undeclared field")]
    [InlineData("beforeCreate", "old.status == 'ready'", "an image the point lacks")]
    [InlineData("beforeDelete", "new.status == 'ready'", "an image the point lacks")]
    [InlineData("beforeCreate", "changed(status)", "changed outside an update")]
    [InlineData("afterCreate", "'manager' in @user.roles", "a role after the commit")]
    [InlineData("beforeUpdate", "'nobody' in @user.roles", "an undeclared role")]
    [InlineData("beforeUpdate", "new.status == 'bogus'", "an enum value not declared")]
    [InlineData("beforeUpdate", "new.quantity == -1", "a negative number")]
    [InlineData("beforeUpdate", "new.quantity == 'x'", "a quoted literal on a number field")]
    [InlineData("beforeUpdate", "new.quantity == 007", "a number the generator does not spell")]
    [InlineData("beforeUpdate", "new.quantity == 99999999999999999999", "a number the core cannot read (the form would refuse it)")]
    [InlineData("beforeUpdate", "new.title < 'a'", "string relational")]
    [InlineData("beforeUpdate", "has(new.title)", "presence on a required field")]
    [InlineData("beforeUpdate", "new.due_on == 'x'", "a literal for a moment")]
    [InlineData("afterUpdate", "new.status == 'ready' && old.status != 'ready' && new.notify_customer", "a bare boolean (spec D5)")]
    [InlineData("beforeUpdate", "new.title == 'a\\x'", "an escape the lexer does not read")]
    [InlineData("beforeUpdate", "new.title == 'a\tb'", "a raw tab the generator escapes")]
    [InlineData("beforeUpdate", "new.title == 'admin‮'", "a direction override (the form would refuse it)")]
    [InlineData("beforeUpdate", "new.title == 'x\U000E0041'", "a tag character (the form would refuse it)")]
    [InlineData("beforeUpdate", "new.title == 'bell\u0007'", "a control character (the form would refuse it)")]
    [InlineData("beforeUpdate", "new.title == 'x'\n", "a trailing line feed")]
    [InlineData("beforeUpdate", " ", "whitespace is not the empty condition")]
    [InlineData("beforeUpdate", "new.title == 'open", "an unclosed quote")]
    [InlineData("beforeUpdate", "new.title == 'x' && ", "a dangling joiner")]
    [InlineData("beforeUpdate", "(new.title == 'x')", "parentheses the generator does not write")]
    public void A_text_the_generator_would_not_write_stays_text(string point, string text, string because)
        => ConditionText.Recognize(text, point, _scope).ShouldBeNull(because);

    [Fact]
    public void A_condition_longer_than_a_condition_may_be_stays_text()
    {
        var text = $"new.title == '{new string('x', ConditionTable.MaxConditionLength)}'";

        ConditionText.Recognize(text, "beforeUpdate", _scope).ShouldBeNull();
    }
}
