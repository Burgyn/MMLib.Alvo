using MMLib.Alvo.Admin.Components.Schema;

namespace MMLib.Alvo.Admin.Tests.Schema;

/// <summary>
/// A field rename inside its own entity's CEL: a column reference is rewritten, nothing else is — and where that
/// cannot be told, the expression is left alone and named (deviation 1 in the plan).
/// </summary>
public class CelNamesTests
{
    [Theory]
    [InlineData("status == 'status'", "state == 'status'")]
    [InlineData("new.status != old.status", "new.state != old.state")]
    [InlineData("changed(status) && has(new.status)", "changed(state) && has(new.state)")]
    [InlineData("@user.status == status", "@user.status == state")]
    [InlineData("other.status == 1", "other.status == 1")]
    [InlineData("statuses == 1 || status_code == 2", "statuses == 1 || status_code == 2")]
    [InlineData("status(1) == 2", "status(1) == 2")]
    [InlineData("status == 'it\\'s status'", "state == 'it\\'s status'")]
    [InlineData("lowerAscii(new.status) == 'x'", "lowerAscii(new.state) == 'x'")]
    [InlineData("status+1 > 2", "state+1 > 2")]
    [InlineData("!status", "!state")]
    [InlineData("(status)", "(state)")]
    [InlineData("new . status == old .status", "new . state == old .state")]
    [InlineData("x.new.status == 1", "x.new.status == 1")]
    [InlineData("status == '.all(x, x)'", "state == '.all(x, x)'")]
    public void Only_a_reference_to_the_column_is_renamed(string cel, string renamed)
        => CelNames.Rename(cel, "status", "state").ShouldBe(renamed);

    [Theory]
    [InlineData("items.all(x, x.status == 'open')")]
    [InlineData("status == '''a'''")]
    public void An_expression_whose_names_cannot_be_told_apart_is_declined(string cel)
        => CelNames.Rename(cel, "status", "state").ShouldBeNull();

    [Theory]
    [InlineData("in")]
    [InlineData("has")]
    public void A_field_named_like_a_reserved_word_is_declined(string field)
        => CelNames.Rename($"true == {field}", field, "inside").ShouldBeNull();

    /// <summary>A keyword as the new name would turn <c>is_public || …</c> into <c>true || …</c> — a rule that still compiles.</summary>
    [Theory]
    [InlineData("true")]
    [InlineData("false")]
    [InlineData("null")]
    [InlineData("in")]
    [InlineData("has")]
    public void A_rename_to_a_reserved_word_is_declined(string to)
        => CelNames.Rename("is_public || owner_id == @user.id", "is_public", to).ShouldBeNull();

    [Theory]
    [InlineData("items.all(x, x.status)", true)]
    [InlineData("statuses", false)]
    public void A_declined_expression_may_still_be_named(string cel, bool names)
        => CelNames.MayName(cel, "status").ShouldBe(names);

    [Theory]
    [InlineData("math.round(new.math) > 1", "math.round(new.amount) > 1")]
    [InlineData("math.round(math)", "math.round(amount)")]
    [InlineData("math . round (math)", "math . round (amount)")]
    [InlineData("math. round(math)", "math. round(amount)")]
    [InlineData("'math.round(math)' == math", "'math.round(math)' == amount")]
    public void A_namespace_before_a_dot_is_never_renamed_as_a_field(string cel, string renamed) =>
        CelNames.Rename(cel, "math", "amount").ShouldBe(renamed);

    /// <summary>A space after the dot does not make the name before it a column, nor the member after it one.</summary>
    [Theory]
    [InlineData("f. x", "f", "g", "f. x")]
    [InlineData("f. x", "x", "y", "f. x")]
    [InlineData("f == 'f.x(f)'", "f", "g", "g == 'f.x(f)'")]
    public void A_name_beside_a_spaced_dot_or_inside_a_dotted_call_in_a_string_is_left_alone(string cel, string from, string to, string renamed) =>
        CelNames.Rename(cel, from, to).ShouldBe(renamed);

    [Fact]
    public void A_function_member_after_the_namespace_is_never_renamed() =>
        CelNames.Rename("math.round(new.round)", "round", "rounded").ShouldBe("math.round(new.rounded)");
}
